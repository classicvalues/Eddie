// <eddie_source_header>
// This file is part of Eddie/AirVPN software.
// Copyright (C)2014-2016 AirVPN (support@airvpn.org) / https://airvpn.org
//
// Eddie is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Eddie is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with Eddie. If not, see <http://www.gnu.org/licenses/>.
// </eddie_source_header>

#include <cstring>

#include <fcntl.h>

#include <linux/fs.h>
#include <sys/ioctl.h>
#include <sys/stat.h>
#include <unistd.h>

#include <sys/prctl.h> // for prctl()
#include <sys/types.h> // for signal()
#include <signal.h> // for signal()

#include <arpa/inet.h> // for inet_pton()

#include <sstream>

/*
// Eddie 2.19.3, some users report issue with zfs compression

#ifndef EDDIE_NOLZMA
#include "loadmod.h"
#endif
*/

#include "impl.h"

// --------------------------
// Virtual
// --------------------------

std::string serviceName = "eddie-elevated";
std::string serviceDesc = "Eddie Elevation";
std::string systemdPath = "/usr/lib/systemd/system";
std::string systemdUnitName = serviceName + ".service";
std::string systemdUnitPath = systemdPath + "/" + systemdUnitName;

int Impl::Main()
{
	signal(SIGINT, SIG_IGN); // If Eddie is executed as terminal, and receive a Ctrl+C, elevated are terminated before child process (if 'spot'). Need a better solution.
	signal(SIGHUP, SIG_IGN); // Signal of reboot, ignore, container will manage it

	prctl(PR_SET_PDEATHSIG, SIGHUP); // Any child process will be killed if this process died, Linux specific

	return IPosix::Main();
}

void Impl::Do(const std::string& commandId, const std::string& command, std::map<std::string, std::string>& params)
{
	if (command == "compatibility-profiles")
	{
		const std::string& dataPath = params["path-data"];

		if (FsFileExists(dataPath) == false)
		{
			// Rename old .airvpn to .eddie if exists, and change owner (<2.17.3 are root-only)
			std::string oldPath = dataPath;
			size_t pos = oldPath.find_last_of("/");
			if (pos != std::string::npos)
			{
				oldPath = oldPath.substr(0, pos) + "/../.airvpn";
				if (FsFileExists(oldPath))
				{
					const std::string& newOwner = params["owner"];
					FsFileMove(oldPath, dataPath);
					std::vector<std::string> args;
					args.push_back("-R");
					args.push_back(newOwner);
					args.push_back(dataPath);
					std::string stdout;
					std::string stderr;
					Exec("chown", args, false, "", stdout, stderr);
				}
			}
		}
	}
	else if (command == "file-immutable-set")
	{
		std::string path = params["path"];
		int flag = atoi(params["flag"].c_str());

		int result = FileImmutableSet(path, flag);

		ReplyCommand(commandId, std::to_string(result));
	}
	else if (command == "dns-flush")
	{
		int pidSystemD = GetProcessIdOfName("systemd");

		std::map<std::string, int> restarted;
		std::vector<std::string> services = StringToVector(params["services"], ';');

		if (pidSystemD != 0)
		{
			std::string servicePath = FsLocateExecutable("service");
			std::string systemctlPath = FsLocateExecutable("systemctl");

			if ((servicePath != "") && (systemctlPath != ""))
			{
				ExecResult systemCtlListUnits = ExecEx2(systemctlPath, "list-units", "--no-pager");
				if (systemCtlListUnits.exit != 0)
				{
					std::vector<std::string> lines = StringToVector(systemCtlListUnits.out, '\n');
					for (std::vector<std::string>::const_iterator l = lines.begin(); l != lines.end(); ++l)
					{
						std::string line = *l;
						for (std::vector<std::string>::const_iterator iS = services.begin(); iS != services.end(); ++iS)
						{
							std::string service = *iS;
							if (restarted.find(service) != restarted.end())
								continue;
							if (StringStartsWith(line, service + ".service") == false)
								continue;
							if (StringContain(line, " running ") == false)
								continue;

							LogRemote("Flush DNS - " + service + " via systemd");
							ExecEx2(servicePath, StringEnsureFileName(service), "restart");
							restarted[service] = 1;
						}
					}
				}
			}

			// Special case - Not need, restarted above
			/*
			std::string systemdResolvePath = FsLocateExecutable("systemd-resolve");
			if (systemdResolvedPath != "")
				ExecOut1(systemdResolvedPath, "--flush-caches");
			*/
		}

		// InitD
		{
			for (std::vector<std::string>::const_iterator i = services.begin(); i != services.end(); ++i)
			{
				std::string service = *i;
				if (restarted.find(service) == restarted.end())
				{
					if (FsFileExists("/etc/init.d/" + service))
					{
						LogRemote("Flush DNS - " + service + " via init.d");
						ExecEx1("/etc/init.d/" + service, "restart");
					}
				}
			}
		}

		// Other specific approach
		{
			for (std::vector<std::string>::const_iterator i = services.begin(); i != services.end(); ++i)
			{
				std::string service = *i;
				if (restarted.find(service) == restarted.end())
				{
					if (service == "nscd")
					{
						// Special case
						// On some system, for example Fedora, nscd caches are saved to disk,
						// located in /var/db/nscd, and not flushed with a simple restart. 
						std::string nscdPath = FsLocateExecutable("nscd", false);
						if (nscdPath != "")
						{
							LogRemote("Flush DNS - nscd");
							ExecEx1(nscdPath, "--invalidate=hosts");
						}
					}
				}
			}
		}

		ReplyCommand(commandId, "1");
	}
	else if (command == "dns-switch-rename-do")
	{
		std::string resolvconf = params["text"];

		if (FsFileExists("/etc/resolv.conf.eddie") == false)
		{
			if (FsFileExists("/etc/resolv.conf"))
			{
				if (FsFileMove("/etc/resolv.conf", "/etc/resolv.conf.eddie") == false)
					ThrowException("Move fail");
			}
		}
		if (FsFileWriteText("/etc/resolv.conf", resolvconf) == false)
			ThrowException("Write fail");
		chmod("/etc/resolv.conf", S_IRUSR | S_IWUSR | S_IRGRP | S_IROTH);
	}
	else if (command == "dns-switch-rename-restore")
	{
		if (FsFileExists("/etc/resolv.conf.eddie"))
		{
			if (FsFileExists("/etc/resolv.conf"))
				FsFileDelete("/etc/resolv.conf");
			FsFileMove("/etc/resolv.conf.eddie", "/etc/resolv.conf");
			ReplyCommand(commandId, "1");
		}
		else
			ReplyCommand(commandId, "0");
	}
	else if (command == "ipv6-block")
	{
		std::vector<std::string> interfaces = FsFilesInPath("/proc/sys/net/ipv6/conf");
		for (std::vector<std::string>::const_iterator i = interfaces.begin(); i != interfaces.end(); ++i)
		{
			std::string interfaceName = *i;

			if (interfaceName == "all") // When switch all, all others autoswitch. Prefer a specific approach.
				continue;

			if (interfaceName == "lo")
				continue;

			std::string curVal = StringTrim(FsFileReadText("/proc/sys/net/ipv6/conf/" + interfaceName + "/disable_ipv6"));

			if (curVal == "1")
			{
				// Nothing to do
			}
			else if (curVal == "0")
			{
				ExecResult switchResult = ExecEx2(FsLocateExecutable("sysctl"), "-w", "net.ipv6.conf." + interfaceName + ".disable_ipv6=1");
				if (switchResult.exit == 0)
				{
					ReplyCommand(commandId, interfaceName);
				}
				else
				{
					ThrowException("Unexpected error when disable IPv6 on interface " + interfaceName);
				}
			}
			else
			{
				// Unexpected, nothing to do
				LogRemote("Unexpected status " + curVal + " of disable_ipv6 for interface " + interfaceName);
			}
		}
	}
	else if (command == "ipv6-restore")
	{
		std::string interfaceName = params["interface"];
		ExecResult switchResult = ExecEx2(FsLocateExecutable("sysctl"), "-w", "net.ipv6.conf." + interfaceName + ".disable_ipv6=0");
		if (switchResult.exit == 0)
		{
		}
		else
		{
			// Ignore
		}
	}
	else if (command == "netlock-nftables-available")
	{
		std::string nft = FsLocateExecutable("nft", false);

		bool available = (nft != "");
		ReplyCommand(commandId, available ? "1" : "0");
	}
	else if (command == "netlock-nftables-activate")
	{
		std::string nft = FsLocateExecutable("nft");

		std::string pathBackup = GetTempPath("netlock_nftables_backup.nft");

		std::string result = "";

		if (FsFileExists(pathBackup))
		{
			ThrowException("Unexpected: Already active");
		}
		else
		{
			/*
			// Try to up kernel module
#ifndef EDDIE_NOLZMA
			int ret = load_kernel_module("nf_tables", "");
			if ((ret != MODULE_LOAD_SUCCESS) && (ret != MODULE_ALREADY_LOADED))
				ThrowException("Unable to initialize nf_tables module");
#else
			*/
			// Exec version, used under Linux Arch, for issue with link LZMA
			std::string modprobePath = FsLocateExecutable("modprobe");
			if (modprobePath != "")
			{
				ExecResult modprobeResult = ExecEx1(modprobePath, "nf_tables");
				if (modprobeResult.exit != 0)
					ThrowException("Unable to initialize nf_tables module");
			}
			/*
			#endif
			*/
			// Backup of current
			std::vector<std::string> args;

			ExecResult shellResultBackup = ExecEx2(nft, "list", "ruleset");
			if (shellResultBackup.exit != 0)
				ThrowException("nft issue: " + GetExecResultDump(shellResultBackup));

			FsFileWriteText(pathBackup, shellResultBackup.out);

			// Apply new
			std::string path = GetTempPath("netlock_nftables_apply.nft");
			FsFileWriteText(path, params["rules"]);
			ExecResult shellResultApply = ExecEx2(nft, "-f", path);
			FsFileDelete(path);
			if (shellResultApply.exit != 0)
				ThrowException("nft issue: " + GetExecResultDump(shellResultApply));
		}

		ReplyCommand(commandId, StringTrim(result));
	}
	else if (command == "netlock-nftables-deactivate")
	{
		std::string nft = FsLocateExecutable("nft");
		std::string path = GetTempPath("netlock_nftables_backup.nft");

		if (FsFileExists(path))
		{
			ExecResult shellResultFlush = ExecEx2(nft, "flush", "ruleset");
			if (shellResultFlush.exit != 0)
				ThrowException("nft issue: " + GetExecResultDump(shellResultFlush));

			ExecResult shellResultRestore = ExecEx2(nft, "-f", path);
			FsFileDelete(path);

			if (shellResultRestore.exit != 0)
				ThrowException("nft issue: " + GetExecResultDump(shellResultRestore));
		}
	}
	else if (command == "netlock-nftables-accept-ip")
	{
		std::string nft = FsLocateExecutable("nft");

		std::string path = "";
		std::vector<std::string> args1;
		std::vector<std::string> args2;
		int nCommands = 1;

		if (params["layer"] == "ipv4")
			path = IptablesExecutable("ipv4", "");
		else if (params["layer"] == "ipv6")
			path = IptablesExecutable("ipv6", "");
		else
			ThrowException("Unknown layer");

		if (params["action"] == "add")
		{
			args1.push_back("insert");
		}
		else if (params["action"] == "del")
		{
			args1.push_back("del");
		}
		else
			ThrowException("Unknown action");

		args1.push_back("rule");

		if (params["layer"] == "ipv4")
			args1.push_back("ip");
		else if (params["layer"] == "ipv6")
			args1.push_back("ip6");
		else
			ThrowException("Unknown layer");

		args1.push_back("filter");
		if (params["direction"] == "in")
			args1.push_back("INPUT");
		else if (params["direction"] == "out")
			args1.push_back("OUTPUT");
		else
			ThrowException("Unknown direction");

		if (params["layer"] == "ipv4")
			args1.push_back("ip");
		else if (params["layer"] == "ipv6")
			args1.push_back("ip6");
		else
			ThrowException("Unknown layer");

		if (params["direction"] == "in")
			args1.push_back("saddr");
		else if (params["direction"] == "out")
			args1.push_back("daddr");
		else
			ThrowException("Unknown direction");

		args1.push_back(StringEnsureCidr(params["cidr"]));

		// Additional rule for incoming
		if (params["direction"] == "in")
		{
			nCommands++;

			args2 = args1;

			args2.push_back("ct");
			args2.push_back("state");
			args2.push_back("established");
			args2.push_back("counter");
			args2.push_back("accept");
		}

		args1.push_back("counter");
		args1.push_back("accept");

		std::string output = "";

		for (int l = 0; l < nCommands; l++)
		{
			std::vector<std::string>* args;
			if (l == 0)
				args = &args1;
			else
				args = &args2;

			ExecResult shellResultRule = ExecEx(nft, *args);
			if (shellResultRule.exit != 0)
				ThrowException("nft issue: " + GetExecResultDump(shellResultRule));

			output += GetExecResultDump(shellResultRule) + "\n";
		}

		ReplyCommand(commandId, StringTrim(output));
	}
	else if (command == "netlock-iptables-available")
	{
		bool available = true;
		if (IptablesExecutable("ipv4", "") == "")
			available = false;
		if (IptablesExecutable("ipv4", "save") == "")
			available = false;
		if (IptablesExecutable("ipv4", "restore") == "")
			available = false;
		if (IptablesExecutable("ipv6", "") == "")
			available = false;
		if (IptablesExecutable("ipv6", "save") == "")
			available = false;
		if (IptablesExecutable("ipv6", "restore") == "")
			available = false;
		ReplyCommand(commandId, available ? "1" : "0");
	}
	else if (command == "netlock-iptables-activate")
	{
		std::string pathIPv4 = GetTempPath("netlock_iptables_backup_ipv4.txt");
		std::string pathIPv6 = GetTempPath("netlock_iptables_backup_ipv6.txt");

		std::string result = "";

		if ((FsFileExists(pathIPv4)) || (FsFileExists(pathIPv6)))
		{
			ThrowException("Unexpected: Already active");
		}
		else
		{
			// Try to up kernel module - For example standard Debian 8 KDE don't have it at boot
			if (params.count("rules-ipv4") > 0)
			{
				/*
				#ifndef EDDIE_NOLZMA
								int ret = load_kernel_module("iptable_filter", "");
								if ((ret != MODULE_LOAD_SUCCESS) && (ret != MODULE_ALREADY_LOADED))
									ThrowException("Unable to initialize iptable_filter module");
				#else
				*/
				// Exec version, used under Linux Arch, for issue with link LZMA
				std::string modprobePath = FsLocateExecutable("modprobe");
				if (modprobePath != "")
				{
					ExecResult modprobeIptable4FilterResult = ExecEx1(modprobePath, "iptable_filter");
					if (modprobeIptable4FilterResult.exit != 0)
						ThrowException("Unable to initialize iptable_filter module");
				}
				/*
				#endif
				*/
			}

			if (params.count("rules-ipv6") > 0)
			{
				/*
				#ifndef EDDIE_NOLZMA
								int ret = load_kernel_module("ip6table_filter", "");
								if ((ret != MODULE_LOAD_SUCCESS) && (ret != MODULE_ALREADY_LOADED))
									ThrowException("Unable to initialize iptable_filter module");
				#else
				*/
				// Exec version, used under Linux Arch, for issue with link LZMA
				std::string modprobePath = FsLocateExecutable("modprobe");
				if (modprobePath != "")
				{
					ExecResult modprobeIptable6FilterResult = ExecEx1(modprobePath, "ip6table_filter");
					if (modprobeIptable6FilterResult.exit != 0)
						ThrowException("Unable to initialize ip6table_filter module");
				}
				/*
				#endif
				*/
			}

			// Backup of current
			std::vector<std::string> args;

			if (params.count("rules-ipv4") > 0)
			{
				std::string backupIPv4 = IptablesExec(IptablesExecutable("ipv4", "save"), args, false, "");
				if (StringContain(backupIPv4, "*filter") == false)
					ThrowException("iptables don't reply, probabily kernel modules issue");
				FsFileWriteText(pathIPv4, backupIPv4);
			}
			if (params.count("rules-ipv6") > 0)
			{
				std::string backupIPv6 = IptablesExec(IptablesExecutable("ipv6", "save"), args, false, "");
				if (StringContain(backupIPv6, "*filter") == false)
					ThrowException("ip6tables don't reply, probabily kernel modules issue");
				FsFileWriteText(pathIPv6, backupIPv6);
			}

			// Apply new
			if (params.count("rules-ipv4") > 0)
				result += IptablesExec(IptablesExecutable("ipv4", "restore"), args, true, params["rules-ipv4"]);
			if (params.count("rules-ipv6") > 0)
				result += IptablesExec(IptablesExecutable("ipv6", "restore"), args, true, params["rules-ipv6"]);
		}

		ReplyCommand(commandId, StringTrim(result));
	}
	else if (command == "netlock-iptables-deactivate")
	{
		std::string pathIPv4 = GetTempPath("netlock_iptables_backup_ipv4.txt");
		std::string pathIPv6 = GetTempPath("netlock_iptables_backup_ipv6.txt");

		std::string result = "";

		if (FsFileExists(pathIPv4))
		{
			std::vector<std::string> args;
			std::string body = FsFileReadText(pathIPv4);
			result += IptablesExec(IptablesExecutable("ipv4", "restore"), args, true, body);

			FsFileDelete(pathIPv4);
		}

		if (FsFileExists(pathIPv6))
		{
			std::vector<std::string> args;
			std::string body = FsFileReadText(pathIPv6);
			result += IptablesExec(IptablesExecutable("ipv6", "restore"), args, true, body);

			FsFileDelete(pathIPv6);
		}

		ReplyCommand(commandId, StringTrim(result));
	}
	else if (command == "netlock-iptables-accept-ip")
	{
		std::string path = "";
		std::vector<std::string> args1;
		std::vector<std::string> args2;
		bool stdinWrite = false;
		std::string stdinBody = "";
		int nCommands = 1;

		if (params["layer"] == "ipv4")
			path = IptablesExecutable("ipv4", "");
		else if (params["layer"] == "ipv6")
			path = IptablesExecutable("ipv6", "");
		else
			ThrowException("Unknown layer");

		std::string directionName = "";
		std::string directionIp = "";
		if (params["direction"] == "in")
		{
			directionName = "INPUT";
			directionIp = "-s";
		}
		else if (params["direction"] == "out")
		{
			directionName = "OUTPUT";
			directionIp = "-d";
		}
		else
			ThrowException("Unknown direction");

		if (params["action"] == "add")
		{
			args1.push_back("-I");
			args1.push_back(directionName);
			args1.push_back("1");
			args1.push_back(directionIp);
		}
		else if (params["action"] == "del")
		{
			args1.push_back("-D");
			args1.push_back(directionName);
			args1.push_back(directionIp);
		}
		else
			ThrowException("Unknown action");

		args1.push_back(StringEnsureCidr(params["cidr"]));
		args1.push_back("-j");
		args1.push_back("ACCEPT");

		// Additional rule for incoming
		if ((directionName == "INPUT") && (directionIp == "-s"))
		{
			nCommands++;
			if (params["action"] == "add")
			{
				args2.push_back("-I");
				args2.push_back("OUTPUT");
				args2.push_back("1");
				args2.push_back("-d");
			}
			else if (params["action"] == "del")
			{
				args2.push_back("-D");
				args2.push_back("OUTPUT");
				args2.push_back("-d");
			}
			else
				ThrowException("Unknown action");

			args2.push_back(StringEnsureCidr(params["cidr"]));
			args2.push_back("-m");
			args2.push_back("state");
			args2.push_back("--state");
			args2.push_back("ESTABLISHED");
			args2.push_back("-j");
			args2.push_back("ACCEPT");
		}

		std::string output = "";

		if (params["action"] == "add") // Add only, Del not implemented yet, need handle detection because iptables-style is not yet implemented: https://wiki.nftables.org/wiki-nftables/index.php/Simple_rule_management#Removing_rules
		{
			for (int l = 0; l < nCommands; l++)
			{
				std::vector<std::string>* args;
				if (l == 0)
					args = &args1;
				else
					args = &args2;

				output += IptablesExec(path, *args, stdinWrite, stdinBody);
			}
		}

		ReplyCommand(commandId, StringTrim(output));
	}
	else if (command == "route-list")
	{
		// TODO: WIP on GetRoutesAsJson and replace

		int n = 0;
		std::string json;

		std::string list;
		ExecResult listIPv4 = ExecEx3(FsLocateExecutable("ip"), "-4", "route", "show");
		if (listIPv4.exit == 0)
			list += listIPv4.out + "\n";

		ExecResult listIPv6 = ExecEx3(FsLocateExecutable("ip"), "-6", "route", "show");
		if (listIPv6.exit == 0)
			list += listIPv6.out + "\n";

		std::vector<std::string> lines = StringToVector(list, '\n');
		for (std::vector<std::string>::const_iterator i = lines.begin(); i != lines.end(); ++i)
		{
			std::string line = StringTrim(*i);

			std::map<std::string, std::string> keypairs;
			std::vector<std::string> fields = StringToVector(line, ' ', true);

			if (fields.size() > 0)
			{
				keypairs["destination"] = fields[0];
				fields.erase(fields.begin());
			}
			while (fields.size() > 1)
			{
				std::string k = fields[0];
				fields.erase(fields.begin());
				std::string v = fields[0];
				fields.erase(fields.begin());
				keypairs[StringToLower(k)] = v;
			}

			// Adapt
			if (keypairs.find("destination") != keypairs.end())
			{
				if (keypairs["destination"] == "default")
				{
					if (keypairs.find("via") != keypairs.end())
					{
						if (StringIsIPv4(keypairs["via"]))
							keypairs["destination"] = "0.0.0.0/0";
						else if (StringIsIPv6(keypairs["via"]))
							keypairs["destination"] = "::/0";
					}
				}
				else if (StringContain(keypairs["destination"], "/") == false)
				{
					// Always full CIDR notation
					if (StringIsIPv4(keypairs["destination"]))
						keypairs["destination"] = keypairs["destination"] + "/32";
					else if (StringIsIPv6(keypairs["destination"]))
						keypairs["destination"] = keypairs["destination"] + "/128";
				}
				keypairs["destination"] = StringIpRemoveInterface(keypairs["destination"]);
			}
			if (keypairs.find("via") != keypairs.end())
			{
				keypairs["gateway"] = StringIpRemoveInterface(keypairs["via"]);
				keypairs.erase("via");
			}
			if (keypairs.find("dev") != keypairs.end())
			{
				keypairs["interface"] = keypairs["dev"];
				keypairs.erase("dev");
			}

			// Build JSON
			if (n > 0)
				json += ",\n";
			json += "{";
			int f = 0;
			for (std::map<std::string, std::string>::iterator it = keypairs.begin(); it != keypairs.end(); ++it)
			{
				std::string k = it->first;
				std::string v = it->second;
				if (f > 0)
					json += ",";
				json += "\"" + k + "\":\"" + v + "\"";
				f++;
			}
			json += "}";
			n++;
		}

		ReplyCommand(commandId, "[" + json + "]");
	}
	else if (command == "route")
	{
		std::vector<std::string> args;

		if (params["layer"] == "ipv4")
			args.push_back("-4");
		else if (params["layer"] == "ipv6")
			args.push_back("-6");
		args.push_back("route");
		args.push_back(StringEnsureAlphaNumeric(params["action"]));
		args.push_back(StringEnsureCidr(params["destination"]));
		if (params.find("gateway") != params.end())
		{
			args.push_back("via");
			args.push_back(StringEnsureIpAddress(params["gateway"]));
		}
		if (params.find("interface") != params.end())
		{
			args.push_back("dev");
			args.push_back(StringEnsureInterfaceName(params["interface"]));
		}
		if (params.find("metric") != params.end())
		{
			args.push_back("metric");
			args.push_back(StringEnsureNumericInt(params["metric"]));
		}

		ExecResult shellResult = ExecEx(FsLocateExecutable("ip"), args);
		if (shellResult.exit != 0)
			ThrowException(GetExecResultDump(shellResult));
	}
	else if (command == "wireguard-version")
	{
		std::string versionPath = "/sys/module/wireguard/version";
		if (FsFileExists(versionPath) == false)
		{
			// Try to up
			std::string modprobePath = FsLocateExecutable("modprobe");
			if (modprobePath != "")
				ExecResult modprobeResult = ExecEx1(modprobePath, "wireguard");
		}

		std::string version = "";
		if (FsFileExists(versionPath))
			version = FsFileReadText(versionPath);
		ReplyCommand(commandId, version);
	}
	else if (command == "wireguard")
	{
		std::string id = params["id"];
		std::string action = params["action"];
		std::string interfaceId = params["interface"].substr(0, 12);

		std::string keypairStopRequest = "wireguard_stop_" + id;

		if (action == "stop")
		{
			m_keypair[keypairStopRequest] = "stop";
		}
		else if (action == "start")
		{
			std::string config = params["config"];
			unsigned long handshakeTimeoutFirst = StringToULong(params["handshake_timeout_first"]);
			unsigned long handshakeTimeoutConnected = StringToULong(params["handshake_timeout_connected"]);

			try
			{
				std::map<std::string, std::string> configmap = IniConfigToMap(config);

				std::string ipPath = FsLocateExecutable("ip");

				ReplyCommand(commandId, "log:setup-start");

				// Try to delete interface if already exists
				if (FsDirectoryExists("/proc/sys/net/ipv4/conf/" + interfaceId))
					ExecEx4(ipPath, "link", "delete", "dev", interfaceId);

				// Configure WireGuard Peer
				wg_peer wgPeer;
				wgPeer.flags = wg_peer_flags(0);
				if (configmap.find("peer.publickey") != configmap.end())
				{
					wgPeer.flags = wg_peer_flags(wgPeer.flags | WGPEER_HAS_PUBLIC_KEY);
					wg_key_from_base64(wgPeer.public_key, configmap["peer.publickey"].c_str());
				}
				if (configmap.find("peer.presharedkey") != configmap.end())
				{
					wgPeer.flags = wg_peer_flags(wgPeer.flags | WGPEER_HAS_PRESHARED_KEY);
					wg_key_from_base64(wgPeer.preshared_key, configmap["peer.presharedkey"].c_str());
				}
				if (configmap.find("peer.persistentkeepalive") != configmap.end())
				{
					wgPeer.flags = wg_peer_flags(wgPeer.flags | WGPEER_HAS_PERSISTENT_KEEPALIVE_INTERVAL);
					wgPeer.persistent_keepalive_interval = StringToInt(configmap["peer.persistentkeepalive"]);
				}
				if (configmap.find("peer.allowedips") != configmap.end())
				{
					wgPeer.flags = wg_peer_flags(wgPeer.flags | WGPEER_REPLACE_ALLOWEDIPS);
					WireGuardParseAllowedIPs(configmap["peer.allowedips"].c_str(), &wgPeer);
				}

				if (configmap.find("peer.endpoint") != configmap.end())
				{
					std::size_t posPort = configmap["peer.endpoint"].find_last_of(":");
					if (posPort == std::string::npos)
						ThrowException("Port not found");
					std::string configEndpointIp = configmap["peer.endpoint"].substr(0, posPort);
					int configEndpointPort = StringToInt(configmap["peer.endpoint"].substr(posPort + 1));

					int err = inet_pton(AF_INET, configEndpointIp.c_str(), &wgPeer.endpoint.addr4.sin_addr);
					if (err == 1)
					{
						wgPeer.endpoint.addr.sa_family = AF_INET;
						wgPeer.endpoint.addr4.sin_port = htons(configEndpointPort);
					}
					else
					{
						if (configEndpointIp.length() > 2)
							configEndpointIp = configEndpointIp.substr(1, configEndpointIp.length() - 2); // remove []
						err = inet_pton(AF_INET6, configEndpointIp.c_str(), &wgPeer.endpoint.addr6.sin6_addr);
						if (err == 1)
						{
							wgPeer.endpoint.addr.sa_family = AF_INET6;
							wgPeer.endpoint.addr6.sin6_port = htons(configEndpointPort);
						}
						else
							ThrowException("Unknown endpoint");
					}
				}

				// Configure WireGuard Device
				wg_device wgDevice;
				wgDevice.flags = wg_device_flags(0);
				strcpy(wgDevice.name, interfaceId.c_str());
				// WGDEVICE_HAS_PUBLIC_KEY ?
				if (configmap.find("interface.privatekey") != configmap.end())
				{
					wgDevice.flags = wg_device_flags(wgDevice.flags | WGDEVICE_HAS_PRIVATE_KEY);
					wg_key_from_base64(wgDevice.private_key, configmap["interface.privatekey"].c_str());
				}
				if (configmap.find("interface.listenport") != configmap.end())
				{
					wgDevice.flags = wg_device_flags(wgDevice.flags | WGDEVICE_HAS_LISTEN_PORT);
					wgDevice.listen_port = StringToInt(configmap["interface.listenport"]);
				}
				if (configmap.find("interface.fwmark") != configmap.end())
				{
					wgDevice.flags = wg_device_flags(wgDevice.flags | WGDEVICE_HAS_FWMARK);
					wgDevice.fwmark = StringToInt(configmap["interface.fwmark"]);
				}

				wgDevice.first_peer = &wgPeer;
				wgDevice.last_peer = &wgPeer;

				if (wg_add_device(wgDevice.name) < 0)
					ThrowException("Unable to add device");

				if (wg_set_device(&wgDevice) < 0)
					ThrowException("Unable to setup device");

				// Add interface addresses
				if (configmap.find("interface.address") != configmap.end())
				{
					std::vector<std::string> interfaceAddresses = StringToVector(configmap["interface.address"], ',');
					for (std::vector<std::string>::const_iterator i = interfaceAddresses.begin(); i != interfaceAddresses.end(); ++i)
					{
						std::string address = *i;

						std::string flagLayer = "";
						if (StringIsIPv4(address))
							flagLayer = "-4";
						else if (StringIsIPv6(address))
							flagLayer = "-6";

						if (flagLayer == "")
							ThrowException("Unknown address type '" + address + "'");

						if (ExecEx6(ipPath, flagLayer, "address", "add", address, "dev", interfaceId).exit != 0)
							ThrowException("Failed to add address '" + address + "'");
					}
				}

				if (configmap.find("interface.mtu") != configmap.end())
				{
					int mtu = StringToInt(configmap["interface.mtu"]);

					if (ExecEx6(ipPath, "link", "set", "mtu", StringFrom(mtu), "dev", interfaceId).exit != 0)
						ThrowException("Failed to set mtu '" + StringFrom(mtu) + "'");
				}

				// Interface up
				if (ExecEx4(ipPath, "link", "set", interfaceId, "up").exit != 0)
					ThrowException("Failed to set interface '" + interfaceId + "' up");

				ReplyCommand(commandId, "log:setup-complete");

				unsigned long handshakeStart = GetTimestampUnix();
				unsigned long handshakeLast = 0;

				for (;;)
				{
					unsigned long handshakeNow = WireGuardLastHandshake(interfaceId);

					if (handshakeLast != handshakeNow)
					{
						if (handshakeLast == 0)
						{
							// First
							ReplyCommand(commandId, "log:handshake-first");
						}

						//ReplyCommand(commandId, "log:last-handshake:" + StringFrom(handshakeNow));
						handshakeLast = handshakeNow;
					}

					unsigned long timeNow = GetTimestampUnix();
					if (handshakeLast > 0)
					{
						unsigned long handshakeDelta = timeNow - handshakeLast;

						if (handshakeDelta > handshakeTimeoutConnected)
						{
							// Too much, suggest disconnect
							ReplyCommand(commandId, "log:handshake-out");
						}
					}
					else
					{
						unsigned long handshakeDelta = timeNow - handshakeStart;

						if (handshakeDelta > handshakeTimeoutFirst)
						{
							// Too much, suggest disconnect
							ReplyCommand(commandId, "log:handshake-out");
						}
					}

					// Check stop requested
					if (m_keypair.find(keypairStopRequest) != m_keypair.end())
					{
						ReplyCommand(commandId, "log:stop-requested");
						break;
					}

					Sleep(1000);
				}
			}
			catch (std::exception& e)
			{
				ReplyCommand(commandId, "err:" + std::string(e.what()));
			}
			catch (...)
			{
				ReplyCommand(commandId, "err:Unknown exception");
			}

			ReplyCommand(commandId, "log:stop-interface");

			if (wg_del_device(interfaceId.c_str()) < 0)
				LogRemote("WireGuard > Unable to delete device");

			m_keypair.erase(keypairStopRequest);

			ReplyCommand(commandId, "log:stop");
		}
	}
	else
	{
		IPosix::Do(commandId, command, params);
	}
}

bool Impl::IsServiceInstalled()
{
	if (FsFileExists(systemdPath))
	{
		if (FsFileExists(systemdUnitPath))
		{
			return true;
		}
	}

	return false;
}

bool Impl::ServiceInstall()
{
	std::string elevatedPath = GetProcessPathCurrent();

	if (FsFileExists(systemdPath))
	{
		if (FsFileExists(systemdUnitPath)) // Remove if exists
		{
			ExecEx2(FsLocateExecutable("systemctl"), "stop", systemdUnitName);
			ExecEx2(FsLocateExecutable("systemctl"), "disable", systemdUnitName);
			FsFileDelete(systemdUnitPath);
		}

		std::string elevatedArgs = "\"mode=service\"";
		std::string integrity = ComputeIntegrityHash(GetProcessPathCurrent(), "");
		if (m_cmdline.find("service_port") != m_cmdline.end())
			elevatedArgs += " \"service_port=" + StringEnsureNumericInt(m_cmdline["service_port"]) + "\"";
		elevatedArgs += " \"integrity=" + StringEnsureIntegrity(integrity) + "\"";

		std::string unit = "";
		unit += "[Unit]\n";
		unit += "Description=" + serviceDesc + "\n";
		unit += "Requires=network.target\n";
		unit += "After=network.target\n";
		unit += "\n";
		unit += "[Service]\n";
		unit += "Type=simple\n";
		unit += "ExecStart=\"" + elevatedPath + "\" " + elevatedArgs + "\n";
		unit += "Restart=always\n";
		unit += "RestartSec=5s\n";
		unit += "TimeoutStopSec=5s\n";
		unit += "User=root\n";
		unit += "Group=root\n";
		unit += "\n";
		unit += "[Install]\n";
		unit += "WantedBy=multi-user.target\n";

		FsFileWriteText(systemdUnitPath, unit);

		ExecResult enableResult = ExecEx2(FsLocateExecutable("systemctl"), "enable", systemdUnitName);
		if (enableResult.exit != 0)
		{
			LogLocal("Enable " + systemdUnitName + " failed");
			return 1;
		}

		ExecResult startResult = ExecEx2(FsLocateExecutable("systemctl"), "start", systemdUnitName);
		if (startResult.exit != 0)
		{
			LogLocal("Start " + systemdUnitName + " failed");
			return 1;
		}

		return 0;
	}
	else
	{
		LogLocal("Can't create service in this OS");
	}

	return 1;
}

bool Impl::ServiceUninstall()
{
	if (FsFileExists(systemdUnitPath))
	{
		ExecEx2(FsLocateExecutable("systemctl"), "stop", systemdUnitName);
		ExecEx2(FsLocateExecutable("systemctl"), "disable", systemdUnitName);
		FsFileDelete(systemdUnitPath);
	}

	return 0;
}

std::string Impl::CheckIfClientPathIsAllowed(const std::string& path)
{
	// Missing under Linux: in other platform (Windows, macOS) check if signature of client match.
	// LocalLog("Checking if " + path + " is allowed");
	return "ok";
}

// --------------------------
// Virtual Pure, OS
// --------------------------

std::string Impl::GetProcessPathCurrent()
{
	char buffer[4096];
	int n = readlink("/proc/self/exe", buffer, sizeof(buffer));
	if (n == -1)
	{
		ThrowException("Fail to detect exe path");
		return "";
	}
	else
	{
		std::string path = std::string(buffer, n);
		return path;
	}
}

std::string Impl::GetProcessPathOfId(int pid)
{
	char fullPath[FILENAME_MAX];
	std::string procExePath = "/proc/" + std::to_string(pid) + "/exe";
	int length = readlink(procExePath.c_str(), fullPath, sizeof(fullPath));
	if (length < 0)
		return "";
	else if (length >= FILENAME_MAX)
		return "";
	else
	{
		std::string path = std::string(fullPath, length);

		// Exception: If mono, detect Assembly path
		bool isMono = false;
		std::string pathMono1 = FsLocateExecutable("mono-sgen", false);
		if ((pathMono1 != "") && (StringStartsWith(path, pathMono1)))
			isMono = true;
		std::string pathMono2 = FsLocateExecutable("mono", false);
		if ((pathMono2 != "") && (StringStartsWith(path, pathMono2)))
			isMono = true;
		if (isMono)
		{
			path = "";
			std::string procCmdLinePath = "/proc/" + std::to_string(pid) + "/cmdline";
			if (FsFileExists(procCmdLinePath))
			{
				std::string cmdline = GetCmdlineOfProcessId(pid);
				std::vector<std::string> fields = StringToVector(cmdline, ' ', false);
				if (fields.size() >= 2)
				{
					for (uint f = 1; f < fields.size(); f++)
					{
						if (StringStartsWith(fields[f], "-") == false)
						{
							path = fields[f];

							// Maybe relative to pid
							if (FsFileExists(path) == false)
							{
								path = FsGetRealPath(GetWorkingDirOfProcessId(pid) + FsPathSeparator + path);
							}

							break;
						}
					}
				}
			}
		}

		return path;
	}
}

// --------------------------
// Private
// --------------------------

int Impl::FileImmutableSet(const std::string& path, const int flag)
{
	const char* filename = path.c_str();
	int result = -1;
	FILE* fp;

	if ((fp = fopen(filename, "r")) != NULL)
	{
		int fd = fileno(fp);

		int attr = 0;
		if (ioctl(fd, FS_IOC_GETFLAGS, &attr) != -1)
		{
			attr = flag ? (attr | FS_IMMUTABLE_FL) : (attr & ~FS_IMMUTABLE_FL);

			if (ioctl(fd, FS_IOC_SETFLAGS, &attr) != -1)
				result = 0;
		}

		fclose(fp);
	}
	return result;
}

std::string Impl::IptablesExecutable(const std::string& layer, const std::string& action)
{
	std::string name = "";

	if (layer == "ipv4")
		name += "iptables";
	else if (layer == "ipv6")
		name += "ip6tables";
	else
		return "";

	if (action != "")
		name += "-" + action;

	std::string legacyName = StringReplaceAll(name, "tables", "tables-legacy");
	std::string legacyPath = FsLocateExecutable(legacyName);
	if (legacyPath != "")
		return legacyPath;
	std::string path = FsLocateExecutable(name);
	return path;
}

std::string Impl::IptablesExec(const std::string& path, const std::vector<std::string>& args, const bool stdinWrite, const std::string stdinBody)
{
	std::string stdout;
	std::string stderr;
	bool done = false;
	std::string output;

	for (int t = 0; t < 10; t++)
	{
		int exitCode = Exec(path, args, stdinWrite, stdinBody, stdout, stderr);

		if (StringContain(StringToLower(stderr), "temporarily unavailable")) // Older Debian (iptables without --wait)
		{
			usleep(500000);
			continue;
		}

		if (StringContain(StringToLower(stderr), "xtables lock")) // Newest Debian (iptables with --wait but not automatic)
		{
			usleep(500000);
			continue;
		}

		if (exitCode != 0)
			ThrowException(stderr);
		else
		{
			done = true;
			output += stdout + "\n";
			break;
		}
	}

	if (done == false)
		ThrowException(stderr);

	return output;
}

std::string Impl::GetRoutesAsJson()
{
	// Not yet used, missing at least conversion of fields in CIDR notation.
	// Objective here is avoid under linux of external exec "route print".
	// Study https://gist.github.com/incebellipipo/6c8657fe1c898ff64a42cddfa6dea6e0 (ipv4 only)

	int n = 0;
	std::string json = "[\n";

	// IPv4
	std::string route4Path = "/proc/net/route";
	if (FsFileExists(route4Path))
	{
		std::vector<std::string> headers;
		std::vector<std::string> lines = StringToVector(FsFileReadText(route4Path), '\n');
		for (size_t iL = 0; iL < lines.size(); iL++)
		{
			std::vector<std::string> fields = StringToVector(lines[iL], '\t');

			if (headers.size() == 0)
				headers = fields;
			else if (fields.size() == headers.size())
			{
				std::map<std::string, std::string> keypairs;
				for (size_t iF = 0; iF < headers.size(); iF++)
				{
					std::string k = StringToLower(headers[iF]);
					std::string v = fields[iF];
					keypairs[k] = v;
				}

				// Adapt
				keypairs["destination_cidr"] = GetRoutesAsJsonHexAddress2string(keypairs["destination"]) + "/" + StringFrom(GetRoutesAsJsonConvertMaskToCidrNetMask(keypairs["mask"]));
				keypairs.erase("destination");
				keypairs.erase("mask");
				keypairs["gateway"] = GetRoutesAsJsonHexAddress2string(keypairs["gateway"]);

				// Build JSON
				if (n > 0)
					json += ",\n";
				json += "{\"iplayer\":\"ipv4\",";
				for (std::map<std::string, std::string>::iterator it = keypairs.begin(); it != keypairs.end(); ++it)
				{
					std::string k = StringToLower(it->first);
					std::string v = it->second;
					json += ",\"" + k + "\":\"" + v + "\"";
					n++;
				}
				//(a<<24) + (b<<16) + (c<<8) + d 

				json += "}";
			}
		}
	}

	// IPv6
	std::string route6Path = "/proc/net/ipv6_route";
	if (FsFileExists(route6Path))
	{
		std::vector<std::string> headers;
		headers.push_back("destination");
		headers.push_back("destination_prefix");
		headers.push_back("source");
		headers.push_back("source_prefix");
		headers.push_back("next_hop");
		headers.push_back("metric");
		headers.push_back("refcnt");
		headers.push_back("use");
		headers.push_back("flags");
		headers.push_back("iface");
		std::vector<std::string> lines = StringToVector(FsFileReadText(route6Path), '\n');
		for (size_t iL = 0; iL < lines.size(); iL++)
		{
			std::vector<std::string> fields = StringToVector(lines[iL], ' ');

			if (fields.size() == headers.size())
			{
				std::map<std::string, std::string> keypairs;
				for (size_t iF = 0; iF < headers.size(); iF++)
				{
					std::string k = StringToLower(headers[iF]);
					std::string v = fields[iF];
					keypairs[k] = v;
				}

				// Adapt
				keypairs["source_cidr"] = GetRoutesAsJsonHexAddress2string(keypairs["source"]) + "/" + StringFrom(GetRoutesAsJsonConvertHexPrefixToCidrNetMask(keypairs["source_prefix"]));
				keypairs.erase("source");
				keypairs.erase("source_prefix");
				keypairs["destination_cidr"] = GetRoutesAsJsonHexAddress2string(keypairs["destination"]) + "/" + StringFrom(GetRoutesAsJsonConvertHexPrefixToCidrNetMask(keypairs["destination_prefix"]));
				keypairs.erase("destination");
				keypairs.erase("destination_prefix");
				keypairs["next_hop"] = GetRoutesAsJsonHexAddress2string(keypairs["next_hop"]);

				// Build JSON
				if (n > 0)
					json += ",\n";
				json += "{\"iplayer\":\"ipv4\",";
				for (std::map<std::string, std::string>::iterator it = keypairs.begin(); it != keypairs.end(); ++it)
				{
					std::string k = it->first;
					std::string v = it->second;
					json += ",\"" + k + "\":\"" + v + "\"";
					n++;
				}
				//(a<<24) + (b<<16) + (c<<8) + d 

				json += "}";
			}
		}
	}

	// End

	json += "\n]";

	return json;
}

std::string Impl::GetRoutesAsJsonHexAddress2string(const std::string& v)
{
	std::string result;
	if (v.length() == 8) // IPv4
	{
		int iparr[4], j = 0;
		for (unsigned int i = v.length(); i > 0; i -= 2, j++)
		{
			std::stringstream iss;
			auto tmp = v.substr(i - 2, 2);
			iss << tmp;
			iss >> std::hex >> iparr[j];
		}

		for (int i = 0; i < 4; i++) {
			result += (i == 3) ? std::to_string(iparr[i]) : std::to_string(iparr[i]) + ".";
		}
	}
	else if (v.length() == 32)
	{
		result = v.substr(0, 4) + ":" + v.substr(4, 4) + ":" + v.substr(8, 4) + ":" + v.substr(12, 4) + ":" + v.substr(16, 4) + ":" + v.substr(20, 4) + ":" + v.substr(24, 4) + ":" + v.substr(28, 4);
	}

	// TODO: Normalize/shorten

	return StringIpNormalize(result);
}

int Impl::GetRoutesAsJsonConvertMaskToCidrNetMask(const std::string& v)
{
	return 0; // TODO
}

int Impl::GetRoutesAsJsonConvertHexPrefixToCidrNetMask(const std::string& v)
{
	unsigned int x;
	std::stringstream ss;
	ss << std::hex << v;
	ss >> x;
	return x;
}

unsigned long Impl::WireGuardLastHandshake(const std::string& interfaceId)
{
	char* device_names, * device_name;
	size_t len;
	bool found = false;
	unsigned long lastHandshake = 0;

	device_names = wg_list_device_names();
	if (!device_names)
		return 0;

	wg_for_each_device_name(device_names, device_name, len)
	{
		wg_device* device;
		wg_peer* peer;

		if (wg_get_device(&device, device_name) < 0)
		{
			continue;
		}

		if (strcmp(device_name, interfaceId.c_str()) == 0)
		{
			wg_for_each_peer(device, peer)
			{
				lastHandshake = peer->last_handshake_time.tv_sec;
				found = true;
			}
		}

		wg_free_device(device);
	}

	free(device_names);

	if (found == false)
		ThrowException("interface '" + interfaceId + "' disappear");

	return lastHandshake;
}

void Impl::WireGuardParseAllowedIPs(const char* allowed_ips, wg_peer* peer)
{
	struct wg_allowedip* latestAllowedIp = NULL;
	int err = 0;

	std::vector<std::string> ips = StringToVector(allowed_ips, ',');
	for (std::vector<std::string>::const_iterator iip = ips.begin(); iip != ips.end(); ++iip)
	{
		struct wg_allowedip* currentAllowedIp;
		char buf[INET6_ADDRSTRLEN];

		std::vector<std::string> parts = StringToVector(*iip, '/');
		if (parts.size() != 2)
			continue;

		currentAllowedIp = new wg_allowedip();

		err = inet_pton(AF_INET, parts[0].c_str(), buf);
		if (err == 1)
		{
			currentAllowedIp->family = AF_INET;
			memcpy(&currentAllowedIp->ip4, buf, sizeof(currentAllowedIp->ip4));
		}
		else
		{
			err = inet_pton(AF_INET6, parts[0].c_str(), buf);
			if (err == 1)
			{
				currentAllowedIp->family = AF_INET6;
				memcpy(&currentAllowedIp->ip6, buf, sizeof(currentAllowedIp->ip6));
			}
		}

		currentAllowedIp->cidr = StringToInt(parts[1]);

		if (err != 1) {
			delete currentAllowedIp;
			continue;
		}

		if (latestAllowedIp == NULL)
			peer->first_allowedip = currentAllowedIp;
		else
			latestAllowedIp->next_allowedip = currentAllowedIp;
		latestAllowedIp = currentAllowedIp;
	}

	peer->last_allowedip = latestAllowedIp;
}

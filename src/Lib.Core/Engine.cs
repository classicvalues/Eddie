﻿// <eddie_source_header>
// This file is part of Eddie/AirVPN software.
// Copyright (C)2014-2019 AirVPN (support@airvpn.org) / https://airvpn.org
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Eddie.Core
{
	public class Engine : Eddie.Core.Thread
	{
		public static Engine Instance;

		public Json Manifest;

		public bool ConsoleMode = false;

		public bool Terminated = false;

		public delegate void TerminateHandler();
		public event TerminateHandler TerminateEvent;

		public EngineCommandLine StartCommandLine;

		private string m_pathProfile = "";
		private string m_pathData = "";

		private Session m_threadSession;
		private UiManager m_uiManager;
		private Options m_options;
		private Storage m_storage;
		private Stats m_stats;
		private ProvidersManager m_providersManager;
		private LogsManager m_logsManager;
		private JobsManager m_jobsManager;
		private NetworkLockManager m_networkLockManager;
		private PingManager m_pingManager;
		private Webserver m_webserver;
		private Elevated.IElevated m_elevated;


		private Dictionary<string, ConnectionInfo> m_connections = new Dictionary<string, ConnectionInfo>();
		private Dictionary<string, AreaInfo> m_areas = new Dictionary<string, AreaInfo>();
		private bool m_serversInfoUpdated = false;
		private bool m_areasInfoUpdated = false;
		protected int m_breakRequests = 0;
		//private TimeDelta m_tickDeltaUiRefreshQuick = new TimeDelta();
		private TimeDelta m_tickDeltaUiRefreshFull = new TimeDelta();



		public enum ActionService
		{
			None = 0,
			Connect = 1,
			Disconnect = 2,
			Login = 3,
			Logout = 4,
			NetLockIn = 5,
			NetLockOut = 6
		}

		public enum RefreshUiMode
		{
			None = 0,
			Stats = 1,
			Full = 2,
			MainMessage = 3, // Clodo, TOCLEAN?
			Log = 4 // Clodo, TOCLEAN?
		}

		private List<ActionService> m_actionsList = new List<ActionService>();
		private string m_mainStatusMessage = "";
		private bool m_mainStatusCancel = false;
		//private bool m_connected = false;

		public ConnectionInfo CurrentServer;
		public ConnectionInfo NextServer;
		public bool SwitchServer;

		public Engine(string environmentCommandLine) : base(false)
		{
			Instance = this;

			StartCommandLine = new EngineCommandLine(environmentCommandLine);

			m_uiManager = new UiManager();
			m_logsManager = new LogsManager();
		}

		public UiManager UiManager
		{
			get
			{
				return m_uiManager;
			}
		}

		public Options Options
		{
			get
			{
				return m_options;
			}
		}

		public Storage Storage
		{
			get
			{
				return m_storage;
			}
		}

		public Stats Stats
		{
			get
			{
				return m_stats;
			}
		}

		public ProvidersManager ProvidersManager
		{
			get
			{
				return m_providersManager;
			}
		}

		public LogsManager Logs
		{
			get
			{
				return m_logsManager;
			}
		}

		public JobsManager JobsManager
		{
			get
			{
				return m_jobsManager;
			}
		}

		public NetworkLockManager NetworkLockManager
		{
			get
			{
				return m_networkLockManager;
			}
		}

		public PingManager PingManager
		{
			get
			{
				return m_pingManager;
			}
		}

		public Webserver Webserver
		{
			get
			{
				return m_webserver;
			}
		}

		public Elevated.IElevated Elevated
		{
			get
			{
				return m_elevated;
			}
		}

		public Session Session
		{
			get
			{
				return m_threadSession;
			}
		}

		public ConnectionTypes.IConnectionType Connection
		{
			get
			{
				if (m_threadSession == null)
					return null;
				return m_threadSession.Connection;
			}
		}

		public Dictionary<string, ConnectionInfo> Connections
		{
			get
			{
				return m_connections;
			}
		}

		public Dictionary<string, AreaInfo> Areas
		{
			get
			{
				return m_areas;
			}
		}

		public Providers.Service AirVPN // TOFIX, for compatibility
		{
			get
			{
				foreach (Providers.IProvider provider in ProvidersManager.Providers)
				{
					if ((provider.Enabled) && (provider.Code == "AirVPN"))
						return provider as Providers.Service;
				}
				return null;
			}
		}

		public bool MainStepInit()
		{
			try
			{
				if (LanguageManager.Init() == false)
				{
					Logs.Log(LogType.Fatal, "Fatal: Unable to locate resources files in " + GetPathResources());
					return false;
				}

				StartCommandLine.Init();

				if (OnInit() == false)
					return false;

				if (Platform.Instance.OnInit() == false)
					return false;

				if (Platform.Instance.OnCheckSingleInstance() == false)
				{
					Logs.Log(LogType.Fatal, LanguageManager.GetText("OsInstanceAlreadyRunning"));
					return false;
				}

				Logs.Log(LogType.Verbose, "Eddie version: " + GetVersionShow() + " / " + Platform.Instance.GetSystemCode() + ", System: " + Platform.Instance.GetCode() + ", Name: " + Platform.Instance.GetName() + ", Version: " + Platform.Instance.GetVersion() + ", Mono/.Net: " + Platform.Instance.GetNetFrameworkVersion());
				if (StartCommandLine.Params.Count != 0)
					Logs.Log(LogType.Verbose, "Command line arguments (" + StartCommandLine.Params.Count.ToString() + "): " + StartCommandLine.GetFull());

				// Init Elevation
				{
					UiManager.Broadcast("init.step", "message", LanguageManager.GetText("InitStepWaitSystemPrivileges"));

					Platform.Instance.WaitService();

					UiManager.Broadcast("init.step", "message", LanguageManager.GetText("InitStepConnectSystemPrivileges"));

					m_elevated = Platform.Instance.StartElevated();
					if (m_elevated == null)
					{
						return false; // Never expected, StartElevated throw exception if something fail.
					}

					m_elevated.DoCommandSync("bin-path-add", "path", GetPathTools());

					Platform.Instance.OnElevated();
				}

				UiManager.Broadcast("init.step", "message", LanguageManager.GetText("InitStepInitialization"));

				// Compute paths
				{
					m_pathProfile = StartCommandLine.Get("profile", "default") + ".profile";
					m_pathProfile = CompatibilityManager.AdaptProfileNameOption(m_pathProfile);

					string pathApp = Platform.Instance.GetApplicationPath();

					// Compute data path
					m_pathData = StartCommandLine.Get("path", "");
					if (m_pathData == "")
						m_pathData = Platform.Instance.GetDefaultDataPath();

					if (m_pathData == "home")
						m_pathData = Platform.Instance.GetUserPath();
					else if (m_pathData == "program")
						m_pathData = pathApp;
					else if (m_pathData == "") // Detect
					{
						if (Platform.Instance.HasAccessToWrite(pathApp)) // Windows portable edition, for example
						{
							m_pathData = pathApp;
						}
						else
						{
							m_pathData = Platform.Instance.GetUserPath();
						}
					}

					CompatibilityManager.Profiles(pathApp, m_pathData);

					Platform.Instance.DirectoryEnsure(m_pathData);

					// Compute profile
					if (Platform.Instance.IsPath(m_pathProfile))
					{
						// Is a path
						FileInfo fi = new FileInfo(Platform.Instance.NormalizePath(m_pathProfile));
						if (StartCommandLine.Get("path", "") == "")
							m_pathData = fi.DirectoryName;
						m_pathProfile = fi.FullName;
					}
					else
					{
						m_pathProfile = m_pathData + Platform.Instance.DirSep + m_pathProfile;
					}
				}

				StartCommandLine.Check();

				// Providers
				UiManager.Broadcast("init.step", "message", LanguageManager.GetText("InitStepLoadingProviders"));
				m_providersManager = new ProvidersManager();
				if (m_providersManager.Init() == false)
				{
					Logs.Log(LogType.Fatal, "Fatal: Unable to initialize at least one provider.");
					return false;
				}

				CountriesManager.Init();

				m_stats = new Core.Stats();

				return true;
			}
			catch (Exception ex)
			{
				Logs.LogFatal(ex);
				return false;
			}
		}

		public bool MainStepProfile()
		{
			try
			{
				if (m_storage != null)
					throw new Exception("Unexpected profile already init");

				UiManager.Broadcast("init.step", "message", LanguageManager.GetText("InitStepLoadingData"));

				CompatibilityManager.FixOldProfilePath(GetProfilePath()); // 2.15

				m_options = new Options();

				m_storage = new Storage();
				m_storage.SavePath = GetProfilePath();
				if (Platform.Instance.FileExists(GetProfilePath()) == false)
				{
					Logs.Log(LogType.Verbose, LanguageManager.GetText("OptionsNotFound"));
				}
				else
				{
					if (m_storage.Load() == false)
					{
						m_storage = null;
						return false;
					}
					else
					{
						Logs.Log(LogType.Verbose, LanguageManager.GetText("OptionsRead", GetProfilePath()));
					}
				}

				LanguageManager.SetIso(Options.Get("language.iso"));

				m_providersManager.Load();

				/* // TOCLEAN - Moved at top (2.21)
				if (Options.GetBool("os.single_instance") == true)
				{
					if (Platform.Instance.OnCheckSingleInstance() == false)
					{
						Logs.Log(LogType.Fatal, LanguageManager.GetText("OsInstanceAlreadyRunning"));
						return false;
					}
				}
				*/

				if (Webserver.GetPath() != "")
				{
					if (Options.GetBool("webui.enabled") == true)
					{
						UiManager.Broadcast("init.step", "message", LanguageManager.GetText("InitStepStartingWebserver"));

						m_webserver = new Webserver();
						m_webserver.Start();
					}
				}

				GenerateManifest();
				Json j = Manifest.Clone();
				j["command"].Value = "ui.manifest";
				UiManager.Broadcast(j);

				UiManager.Broadcast("init.step", "message", LanguageManager.GetText("InitStepStartingPlatform"));
				Platform.Instance.OnStart();

				m_jobsManager = new JobsManager();

				m_networkLockManager = new NetworkLockManager();
				m_networkLockManager.Init();

				m_pingManager = new PingManager();
				m_pingManager.Init();

				UiManager.Broadcast("init.step", "message", LanguageManager.GetText("InitStepCheckingSoftware"));
				Software.Checking();
				Software.Log();

				CompatibilityManager.AfterProfile();

				CheckEnvironmentApp();

				UiManager.Broadcast("init.step", "message", LanguageManager.GetText("InitStepCheckRecoveryStatus"));
				Platform.Instance.OnRecoveryAlways();
				Recovery.Load();

				UiManager.Broadcast("init.step", "message", LanguageManager.GetText("InitStepComputeData"));
				PostManifestUpdate();

				UiManager.Broadcast("init.step", "message", LanguageManager.GetText("InitStepAppStartEvents"));
				RunEventCommand("app.start");

				if (Options.GetLower("netlock.mode") != "none")
				{
					if (Options.GetBool("netlock")) // 2.8
					{
						UiManager.Broadcast("init.step", "message", LanguageManager.GetText("NetworkLockActivation"));
						if (m_networkLockManager.Activation() == false)
						{
							if (Options.GetBool("connect"))
							{
								Engine.Instance.Logs.Log(LogType.Fatal, LanguageManager.GetText("NetworkLockActivationConnectStop"));
								Options.SetBool("connect", false);
							}
						}
					}
				}

				WaitMessageClear();

				UiManager.Broadcast("init.step", "message", LanguageManager.GetText("InitStepUi"));
				UiManager.Broadcast("engine.ui");

				if (OnInitUi() == false)
					return false;

				Logs.Log(LogType.Info, LanguageManager.GetText("Ready"));
				UiManager.Broadcast("engine.ready");

				RaiseMainStatus();

				if (ConsoleMode)
					Auth();

				if (Options.GetBool("connect"))
					Connect();

				return true;
			}
			catch (Exception ex)
			{
				Logs.LogFatal(ex);
				return false;
			}
		}

		public void MainStepWork()
		{
			for (; ; )
			{
				if (CancelRequested)
					break;

				OnWork();

				m_uiManager.ProcessOnMainThread();

				if (m_jobsManager != null)
					m_jobsManager.Check();

				Sleep(100);

				// TOFIX: Action need to be converted in uimanager-commands, that already run in the engine thread.
				ActionService currentAction = ActionService.None;

				lock (m_actionsList)
				{
					if (m_actionsList.Count > 0)
					{
						currentAction = m_actionsList[0];
						m_actionsList.RemoveAt(0);
					}
				}

				if (currentAction == ActionService.None)
				{
				}
				else if (currentAction == ActionService.Login)
				{
					Auth();
				}
				else if (currentAction == ActionService.Logout)
				{
					DeAuth();
				}
				else if (currentAction == ActionService.NetLockIn)
				{
					m_networkLockManager.Activation();
					WaitMessageClear();
				}
				else if (currentAction == ActionService.NetLockOut)
				{
					m_networkLockManager.Deactivation(false);
					WaitMessageClear();
				}
				else if (currentAction == ActionService.Connect)
				{
					if (m_threadSession == null)
						SessionStart();
				}
				else if (currentAction == ActionService.Disconnect)
				{
					if (m_threadSession != null)
						SessionStop();
				}

				// Avoid to refresh UI for every ping, for example
				if (m_tickDeltaUiRefreshFull.Elapsed(1000))
				{
					if (m_serversInfoUpdated)
					{
						m_serversInfoUpdated = false;
						OnRefreshUi(RefreshUiMode.Full);
					}

					if (m_areasInfoUpdated)
					{
						m_areasInfoUpdated = false;
						RecomputeAreas();
					}
				}


			}
		}

		public void MainStepDeInit()
		{
			Logs.Log(LogType.Verbose, LanguageManager.GetText("AppShutdownStart"));

			OnDeInit();

			RunEventCommand("app.stop");

			OnDeInit2();

			Logs.Log(LogType.Verbose, LanguageManager.GetText("AppShutdownComplete"));
		}

		public override void OnRun()
		{
			if (MainStepInit())
			{
				if (MainStepProfile())
					MainStepWork();
			}

			MainStepDeInit();

			UiManager.Broadcast("engine.shutdown");
			Terminated = true;
			if (TerminateEvent != null)
				TerminateEvent();
		}

		public virtual bool OnInit()
		{
			return true;
		}

		public virtual bool OnInitUi()
		{
			return true;
		}

		public virtual void OnWork()
		{
		}

		public virtual void OnDeInit()
		{
			SessionStop();

			WaitMessageSet(LanguageManager.GetText("AppExiting"), false);

			if (m_jobsManager != null)
			{
				m_jobsManager.RequestStopSync();
				m_jobsManager = null;
			}

			if (m_networkLockManager != null)
			{
				m_networkLockManager.Deactivation(true);
				m_networkLockManager = null;
			}

			if (m_pingManager != null)
			{
				m_pingManager.Stop();
				m_pingManager = null;
			}

			if (m_webserver != null)
			{
				m_webserver.Stop();
				m_webserver = null;
			}

			TemporaryFiles.Clean();

			Platform.Instance.OnCheckSingleInstanceClear();

			Platform.Instance.OnDeInit();

			if (m_elevated != null)
			{
				m_elevated.Stop();
				m_elevated = null;
			}
		}

		public virtual void OnDeInit2()
		{
			SaveSettings();

			if (m_storage != null)
			{
				if (Options.GetBool("remember") == false)
				{
					DeAuth(); // To remove data like crt/key
				}
			}
		}

		public virtual void OnSettingsChanged()
		{
			OnCheckConnections();

			SaveSettings(); // 2.8

			OnRefreshUi(RefreshUiMode.Full);

			if (m_networkLockManager != null)
				m_networkLockManager.OnUpdateIps();
		}

		public virtual void OnSessionStart()
		{
			RunEventCommand("session.start");

			Platform.Instance.OnSessionStart();
		}

		public virtual void OnSessionStop()
		{
			Platform.Instance.OnSessionStop();

			RunEventCommand("session.stop");
		}

		public virtual void Exit()
		{
			RequestStop();
		}

		public virtual void OnSignal(string signal)
		{
			Engine.Instance.Logs.Log(LogType.Verbose, LanguageManager.GetText("ReceivedOsSignal", signal));
			m_breakRequests++;
			Exit();
		}

		public virtual void OnExitRejected()
		{
			m_breakRequests = 0;
		}

		public string GetVersionShow()
		{
			string v = Constants.VersionDesc;
			if (Constants.VersionBeta)
				v += "beta";
			return v;
		}

		public string GetDataPath()
		{
			return m_pathData;
		}

		public string GetProfilePath()
		{
			return m_pathProfile;
		}

		public string GetPathInData(string filename)
		{
			return m_pathData + Platform.Instance.DirSep + filename;
		}

		public string GetPathResources()
		{
			string pathResources = StartCommandLine.Get("path.resources", "res/");
			pathResources = Platform.Instance.FileGetAbsolutePath(pathResources, Platform.Instance.GetApplicationPath());
			pathResources = Platform.Instance.FileGetPhysicalPath(pathResources);
			return pathResources;
		}

		public string GetPathTools()
		{
			string pathTools = StartCommandLine.Get("path.tools", "");
			if (pathTools == "")
				pathTools = Platform.Instance.GetApplicationPath();
			else
				pathTools = Platform.Instance.FileGetAbsolutePath(pathTools, Platform.Instance.GetApplicationPath());
			pathTools = Platform.Instance.FileGetPhysicalPath(pathTools);
			return pathTools;
		}

		public string LocateResource(string relativePath)
		{
			string pathResources = GetPathResources();
			string pathResource = Platform.Instance.NormalizePath(pathResources + "/" + relativePath);
			if ((File.Exists(pathResource)) || (Directory.Exists(pathResource)))
			{
				return pathResource;
			}
			else
			{
				return Platform.Instance.LocateResource(relativePath);
			}
		}

		public bool AskExitConfirm()
		{
			if (m_breakRequests > 0)
				return false;

			if (Options.GetBool("gui.exit_confirm") == true)
				return true;

			return false;
		}

		public void Connect()
		{
			Engine.Instance.ProvidersManager.DoRefresh(false);

			if ((CanConnect() == true) && (IsConnected() == false) && (IsWaiting() == false))
			{
				lock (m_actionsList)
				{
					m_actionsList.Add(Engine.ActionService.Connect);
				}
			}
		}

		public void Login()
		{
			lock (m_actionsList)
			{
				m_actionsList.Add(Engine.ActionService.Login);
			}
		}

		public void Logout()
		{
			lock (m_actionsList)
			{
				m_actionsList.Add(Engine.ActionService.Logout);
			}
		}

		public void NetLockIn()
		{
			lock (m_actionsList)
			{
				m_actionsList.Add(Engine.ActionService.NetLockIn);
			}
		}

		public void NetLockOut()
		{
			lock (m_actionsList)
			{
				m_actionsList.Add(Engine.ActionService.NetLockOut);
			}
		}

		public void Disconnect()
		{
			lock (m_actionsList)
			{
				m_actionsList.Add(Engine.ActionService.Disconnect);
			}
		}

		public void MarkServersListUpdated()
		{
			// Called for minimal changes, like ping
			m_serversInfoUpdated = true;
		}

		public void MarkAreasListUpdated()
		{
			m_areasInfoUpdated = true;
		}

		public void UpdateConnectedStatus(bool connected)
		{
			lock (this)
			{
				if (connected)
					WaitMessageClear();

				if (!connected)
					Engine.NetworkLockManager.OnVpnDisconnected();

				OnRefreshUi(RefreshUiMode.Full);

				RaiseMainStatus();
			}
		}

		public void WaitMessageClear()
		{
			WaitMessageSet("", false);
		}

		public void WaitMessageSet(string message, bool allowCancel)
		{
			if (message != "")
			{
				if (IsConnected())
					throw new Exception("Unexpected status.");
			}

			lock (this)
			{
				m_mainStatusMessage = message;
				m_mainStatusCancel = allowCancel;

				OnRefreshUi(RefreshUiMode.MainMessage);

				RaiseMainStatus();
			}
		}

		public bool IsConnected()
		{
			if (m_threadSession == null)
				return false;

			if (m_threadSession.Connection == null)
				return false;

			return m_threadSession.GetConnected();
		}

		public bool IsWaiting()
		{
			return (m_mainStatusMessage != "");
		}

		public string WaitMessage
		{
			get
			{
				return m_mainStatusMessage;
			}
		}

		public bool IsWaitingCancelAllowed()
		{
			return (m_mainStatusCancel);
		}

		public bool IsWaitingCancelPending()
		{
			if (m_threadSession == null)
				return false;

			lock (m_threadSession)
			{
				return m_threadSession.CancelRequested;
			}
		}

		// ----------------------------------------------------
		// Logging
		// ----------------------------------------------------

		public virtual void OnLog(LogEntry l)
		{
			// An exception, to have a clean, standard 'man' output without logging header.
			if (Engine.Instance.StartCommandLine.Exists("help"))
			{
				Console.WriteLine(l.Message);
				return;
			}

			if (l.Type >= LogType.InfoImportant)
			{
				if (Storage != null)
				{
					if (Options.GetBool("gui.notifications"))
					{
						Json j = new Json();
						j["command"].Value = "ui.notification";
						j["level"].Value = l.GetTypeString();
						j["message"].Value = l.Message;
						UiManager.Broadcast(j);
					}
				}
			}

			if (l.Type >= LogType.Info)
			{
				RaiseStatus(l.Message);
			}

			if (l.Type != LogType.Realtime)
			{
				string lines = l.GetStringLines().Trim();

				if (StartCommandLine.Get("console.mode", "keys") == "batch")
					Console.WriteLine(lines);
				if (StartCommandLine.Get("console.mode", "keys") == "keys")
					Console.WriteLine(lines);

				if (Options != null)
				{
					lock (Options)
					{
						if (Options.GetBool("log.file.enabled"))
						{
							try
							{
								string logPath = Options.Get("log.file.path").Trim();
								Encoding encoding = Options.GetEncoding("log.file.encoding");

								List<string> paths = Logs.ParseLogFilePath(logPath);
								foreach (string path in paths)
								{
									Directory.CreateDirectory(Path.GetDirectoryName(path));
									string text = Platform.Instance.NormalizeString(lines + "\n");
									Platform.Instance.FileContentsAppendText(path, text, encoding);
								}
							}
							catch (Exception ex)
							{
								Logs.Log(LogType.Warning, LanguageManager.GetText("LogsDisabledForError", ex.Message));
								Options.SetBool("log.file.enabled", false);
							}
						}
					}
				}
			}
		}

		public virtual void OnShowText(string title, string data)
		{
		}

		// Remember: Only when Splash is active
		public virtual string OnAskProfilePassword(bool authFailed)
		{
			throw new Exception("Not implemented");
		}

		// Remember: Only when Splash is active
		public virtual bool OnAskYesNo(string message)
		{
			throw new Exception("Not implemented");
		}

		public virtual Json OnAskExecExternalPermission(Json data)
		{
			Json j = new Json();
			j["allow"].Value = true;
			return j;
		}

		public virtual Credentials OnAskCredentials()
		{
			return null;
		}

		public virtual void OnPostManifestUpdate()
		{
			lock (m_connections)
			{
				foreach (ConnectionInfo infoServer in m_connections.Values)
				{
					infoServer.Deleted = true;
				}
			}

			foreach (Providers.IProvider provider in ProvidersManager.Providers)
			{
				if (provider.Enabled)
				{
					provider.OnBuildConnections();
				}
			}

			lock (m_connections)
			{
				for (; ; )
				{
					bool restart = false;
					foreach (ConnectionInfo infoServer in m_connections.Values)
					{
						if (infoServer.Deleted)
						{
							m_connections.Remove(infoServer.Code);
							restart = true;
							break;
						}
					}

					if (restart == false)
						break;
				}

				// White/black list
				List<string> serversWhiteList = Options.GetList("servers.whitelist");
				List<string> serversBlackList = Options.GetList("servers.blacklist");
				foreach (ConnectionInfo infoConnection in m_connections.Values)
				{
					string code = infoConnection.Code;
					string displayName = infoConnection.DisplayName;

					/*
					if (serversWhiteList.Contains(code))
						infoServer.UserList = ServerInfo.UserListType.WhiteList;
					else if (serversBlackList.Contains(code))
						infoServer.UserList = ServerInfo.UserListType.BlackList;
					else
						infoServer.UserList = ServerInfo.UserListType.None;
					*/
					// 2.11.5
					if (serversWhiteList.Contains(code))
						infoConnection.UserList = ConnectionInfo.UserListType.Whitelist;
					else if (serversWhiteList.Contains(displayName))
						infoConnection.UserList = ConnectionInfo.UserListType.Whitelist;
					else if (serversBlackList.Contains(code))
						infoConnection.UserList = ConnectionInfo.UserListType.Blacklist;
					else if (serversBlackList.Contains(displayName))
						infoConnection.UserList = ConnectionInfo.UserListType.Blacklist;
					else
						infoConnection.UserList = ConnectionInfo.UserListType.None;
				}
			}

			RecomputeAreas();

			OnCheckConnections();

			if (m_jobsManager.Discover != null)
				m_jobsManager.Discover.CheckNow();
		}

		public virtual void OnCheckConnections()
		{
			Dictionary<string, ConnectionInfo> connections;
			lock (Engine.Connections)
				connections = new Dictionary<string, ConnectionInfo>(Engine.Connections);

			foreach (ConnectionInfo connectionInfo in connections.Values)
			{
				connectionInfo.Warnings.Clear();
				if (connectionInfo.WarningOpen != "")
					connectionInfo.WarningAdd(connectionInfo.WarningOpen, ConnectionInfoWarning.WarningType.Warning);
				if (connectionInfo.WarningClosed != "")
					connectionInfo.WarningAdd(connectionInfo.WarningClosed, ConnectionInfoWarning.WarningType.Error);

				if ((Engine.Instance.Options.Get("network.entry.iplayer") == "ipv6-only") && (connectionInfo.IpsEntry.CountIPv6 == 0))
					connectionInfo.WarningAdd(LanguageManager.GetText("ConnectionWarningNoEntryIPv6"), ConnectionInfoWarning.WarningType.Error);
				if ((Engine.Instance.Options.Get("network.entry.iplayer") == "ipv4-only") && (connectionInfo.IpsEntry.CountIPv4 == 0))
					connectionInfo.WarningAdd(LanguageManager.GetText("ConnectionWarningNoEntryIPv4"), ConnectionInfoWarning.WarningType.Error);
				if ((Engine.Instance.Options.Get("network.ipv4.mode") == "in") && (connectionInfo.SupportIPv4 == false))
					connectionInfo.WarningAdd(LanguageManager.GetText("ConnectionWarningNoExitIPv4"), ConnectionInfoWarning.WarningType.Error);
				if ((Engine.Instance.GetNetworkIPv6Mode() == "in") && (connectionInfo.SupportIPv6 == false))
					connectionInfo.WarningAdd(LanguageManager.GetText("ConnectionWarningNoExitIPv6"), ConnectionInfoWarning.WarningType.Error);

				try
				{
					ConnectionTypes.IConnectionType connection = connectionInfo.BuildConnection(null);
					connection.CheckForWarnings();
				}
				catch (Exception ex)
				{
					connectionInfo.WarningAdd(ex.Message, ConnectionInfoWarning.WarningType.Error);
				}
			}

			foreach (Providers.IProvider provider in ProvidersManager.Providers)
			{
				if (provider.Enabled)
					provider.OnCheckConnections();
			}
		}

		public virtual void OnRefreshUi()
		{
			OnRefreshUi(RefreshUiMode.Full);
		}

		public virtual void OnRefreshUi(RefreshUiMode mode)
		{
			try
			{
				if ((mode == Core.Engine.RefreshUiMode.Stats) || (mode == Core.Engine.RefreshUiMode.Full))
				{
					if (Engine.IsConnected())
					{
						{
							DateTime DT1 = Engine.Connection.TimeStart;
							DateTime DT2 = DateTime.UtcNow;
							TimeSpan TS = DT2 - DT1;
							string TSText = string.Format("{0:00}:{1:00}:{2:00} - {3}", (int)TS.TotalHours, TS.Minutes, TS.Seconds, LanguageManager.FormatDateShort(DT1.ToLocalTime()));
							Stats.UpdateValue("VpnStart", TSText);
						}

						Stats.UpdateValue("VpnTotalDownload", LanguageManager.FormatBytes(Engine.Connection.BytesRead, false, true));
						Stats.UpdateValue("VpnTotalUpload", LanguageManager.FormatBytes(Engine.Connection.BytesWrite, false, true));

						Stats.UpdateValue("VpnSpeedDownload", LanguageManager.FormatBytes(Engine.Connection.BytesLastDownloadStep, true, true));
						Stats.UpdateValue("VpnSpeedUpload", LanguageManager.FormatBytes(Engine.Connection.BytesLastUploadStep, true, true));

						{
							DateTime DT1 = m_threadSession.StatsStart;
							DateTime DT2 = DateTime.UtcNow;
							TimeSpan TS = DT2 - DT1;
							string TSText = string.Format("{0:00}:{1:00}:{2:00} - {3}", (int)TS.TotalHours, TS.Minutes, TS.Seconds, LanguageManager.FormatDateShort(DT1.ToLocalTime()));
							Stats.UpdateValue("SessionStart", TSText);
						}

						Stats.UpdateValue("SessionTotalDownload", LanguageManager.FormatBytes(m_threadSession.StatsRead, false, true));
						Stats.UpdateValue("SessionTotalUpload", LanguageManager.FormatBytes(m_threadSession.StatsWrite, false, true));
					}
					else
					{
						Stats.UpdateValue("VpnStart", LanguageManager.GetText("StatsNotConnected"));
						Stats.UpdateValue("VpnTotalDownload", LanguageManager.GetText("StatsNotConnected"));
						Stats.UpdateValue("VpnTotalUpload", LanguageManager.GetText("StatsNotConnected"));
						Stats.UpdateValue("VpnSpeedDownload", LanguageManager.GetText("StatsNotConnected"));
						Stats.UpdateValue("VpnSpeedUpload", LanguageManager.GetText("StatsNotConnected"));

						Stats.UpdateValue("SessionStart", LanguageManager.GetText("StatsNotConnected"));
						Stats.UpdateValue("SessionTotalDownload", LanguageManager.GetText("StatsNotConnected"));
						Stats.UpdateValue("SessionTotalUpload", LanguageManager.GetText("StatsNotConnected"));
					}
				}

				if (mode == Core.Engine.RefreshUiMode.Full)
				{
					if (m_jobsManager != null)
					{
						if (m_jobsManager.Latency != null)
						{
							Stats.UpdateValue("Pinger", PingerStats().ToString());
						}

						if (m_jobsManager.Discover != null)
						{
							Stats.UpdateValue("Discovery", m_jobsManager.Discover.GetStatsString().ToString());
						}
					}

					if (Engine.IsConnected())
					{
						Stats.UpdateValue("ServerName", Engine.CurrentServer.DisplayName);
						Stats.UpdateValue("ServerLatency", Engine.CurrentServer.GetLatencyForList());
						Stats.UpdateValue("ServerLocation", Engine.CurrentServer.GetLocationForList());
						Stats.UpdateValue("ServerLoad", Engine.CurrentServer.GetLoadForList());
						Stats.UpdateValue("ServerUsers", Engine.CurrentServer.GetUsersForList());

						Stats.UpdateValue("AccountLogin", Options.Get("login"));
						Stats.UpdateValue("AccountKey", Options.Get("key"));

						Stats.UpdateValue("VpnEntryIP", Engine.Connection.EntryIP.ToString());
						if (Engine.Connection.ExitIPs.CountIPv4 != 0)
							Stats.UpdateValue("VpnExitIPv4", Engine.Connection.ExitIPs.OnlyIPv4.ToString());
						else
							Stats.UpdateValue("VpnExitIPv4", LanguageManager.GetText("NotAvailable"));
						if (Engine.Connection.ExitIPs.CountIPv6 != 0)
							Stats.UpdateValue("VpnExitIPv6", Engine.Connection.ExitIPs.OnlyIPv6.ToString());
						else
							Stats.UpdateValue("VpnExitIPv6", LanguageManager.GetText("NotAvailable"));
						Stats.UpdateValue("VpnType", Engine.Connection.GetTypeName());
						Stats.UpdateValue("VpnProtocol", Engine.Connection.GetProtocolDescription());
						Stats.UpdateValue("VpnPort", Engine.Connection.EntryPort.ToString());
						if (Engine.Connection.RealIp != "")
							Stats.UpdateValue("VpnRealIp", Engine.Connection.RealIp);
						else if (Engine.CurrentServer.SupportCheck)
							Stats.UpdateValue("VpnRealIp", LanguageManager.GetText("CheckingRequired"));
						else
							Stats.UpdateValue("VpnRealIp", LanguageManager.GetText("NotAvailable"));
						Stats.UpdateValue("VpnIp", Engine.Connection.GetVpnIPs().ToString());
						Stats.UpdateValue("VpnDns", Engine.Connection.GetDns().ToString());
						if (Engine.Connection.Interface != null)
							Stats.UpdateValue("VpnInterface", Engine.Connection.Interface.Name);
						else
							Stats.UpdateValue("VpnInterface", "");
						Stats.UpdateValue("VpnDataChannel", Engine.Connection.DataChannel);
						Stats.UpdateValue("VpnControlChannel", Engine.Connection.ControlChannel);
						Stats.UpdateValue("VpnGeneratedConfig", "");
						Stats.UpdateValue("VpnGeneratedConfigPush", "");

						if (Engine.Connection.TimeServer != 0)
							Stats.UpdateValue("SystemTimeServerDifference", (Engine.Connection.TimeServer - Engine.Connection.TimeClient).ToString() + " seconds");
						else if (Engine.CurrentServer.SupportCheck)
							Stats.UpdateValue("SystemTimeServerDifference", LanguageManager.GetText("CheckingRequired"));
						else
							Stats.UpdateValue("SystemTimeServerDifference", LanguageManager.GetText("NotAvailable"));
					}
					else
					{
						Stats.UpdateValue("ServerName", LanguageManager.GetText("StatsNotConnected"));
						Stats.UpdateValue("ServerLatency", LanguageManager.GetText("StatsNotConnected"));
						Stats.UpdateValue("ServerLocation", LanguageManager.GetText("StatsNotConnected"));
						Stats.UpdateValue("ServerLoad", LanguageManager.GetText("StatsNotConnected"));
						Stats.UpdateValue("ServerUsers", LanguageManager.GetText("StatsNotConnected"));

						Stats.UpdateValue("AccountLogin", LanguageManager.GetText("StatsNotConnected"));
						Stats.UpdateValue("AccountKey", LanguageManager.GetText("StatsNotConnected"));

						Stats.UpdateValue("VpnEntryIP", LanguageManager.GetText("StatsNotConnected"));
						Stats.UpdateValue("VpnExitIPv4", LanguageManager.GetText("StatsNotConnected"));
						Stats.UpdateValue("VpnExitIPv6", LanguageManager.GetText("StatsNotConnected"));
						Stats.UpdateValue("VpnType", LanguageManager.GetText("StatsNotConnected"));
						Stats.UpdateValue("VpnProtocol", LanguageManager.GetText("StatsNotConnected"));
						Stats.UpdateValue("VpnPort", LanguageManager.GetText("StatsNotConnected"));
						Stats.UpdateValue("VpnRealIp", LanguageManager.GetText("StatsNotConnected"));
						Stats.UpdateValue("VpnIp", LanguageManager.GetText("StatsNotConnected"));
						Stats.UpdateValue("VpnDns", LanguageManager.GetText("StatsNotConnected"));
						Stats.UpdateValue("VpnInterface", LanguageManager.GetText("StatsNotConnected"));
						Stats.UpdateValue("VpnDataChannel", LanguageManager.GetText("StatsNotConnected"));
						Stats.UpdateValue("VpnControlChannel", LanguageManager.GetText("StatsNotConnected"));
						Stats.UpdateValue("VpnGeneratedConfig", LanguageManager.GetText("StatsNotConnected"));
						Stats.UpdateValue("VpnGeneratedConfigPush", LanguageManager.GetText("StatsNotConnected"));
						Stats.UpdateValue("SystemTimeServerDifference", LanguageManager.GetText("StatsNotConnected"));
					}
				}

				if (mode == RefreshUiMode.Stats)
				{
					UiSendQuickInfo();
				}

				if (mode == RefreshUiMode.Full)
				{
					UiSendStatusInfo();
				}

				if (mode == RefreshUiMode.MainMessage)
				{
					UiSendStatusInfo();
				}
			}
			catch
			{
				// Avoid crash on Mono
			}
		}

		public virtual void OnStatsChange(StatsEntry entry)
		{
		}

		public virtual void OnProviderManifestFailed(Providers.IProvider provider)
		{

		}

		public virtual void OnUnhandledException(string source, Exception ex)
		{
			bool ignore = false;
			string stackTrace = ex.StackTrace.ToString();

			if (stackTrace.IndexOf("System.Windows.Forms.XplatUIX11") != -1)
			{
				// Mono bug https://bugs.debian.org/cgi-bin/bugreport.cgi?bug=742774
				// Mono bug https://bugs.debian.org/cgi-bin/bugreport.cgi?bug=727651
				ignore = true;
			}

			if (stackTrace.IndexOf("System.Windows.Forms.ToolStripItem.OnParentChanged") != -1)
			{
				// Mono bug https://bugs.debian.org/cgi-bin/bugreport.cgi?bug=742774
				// Mono bug https://bugs.debian.org/cgi-bin/bugreport.cgi?bug=727651
				ignore = true;
			}

			// TOFIX: 2.16.2, unknown why happen
			if (ex.Message.Trim() == "The parameter is incorrect.")
				ignore = true;

			if (Terminated)
				ignore = true;

			if (ignore == false)
				Logs.Log(LogType.Fatal, LanguageManager.GetText("UnhandledException") + " - " + source + " - " + ex.Message + " - " + ex.StackTrace.ToString());
		}

		// ----------------------------------------------------
		// Misc
		// ----------------------------------------------------

		public ConnectionInfo GetConnectionInfo(string code, Providers.IProvider provider)
		{
			ConnectionInfo infoConnection = null;
			if (m_connections.ContainsKey(code))
				infoConnection = m_connections[code];
			if (infoConnection == null)
			{
				// Create				
				infoConnection = new ConnectionInfo();
				infoConnection.Provider = provider;
				infoConnection.Code = code;
				lock (m_connections)
				{
					m_connections[code] = infoConnection;
				}
			}
			infoConnection.Deleted = false;
			return infoConnection;
		}

		// Return the list of selected servers, based on settings, filter, ordered by score.
		// Filter can be "earth","europe","nl","castor".
		public List<ConnectionInfo> GetConnections(bool all)
		{
			List<ConnectionInfo> list = new List<ConnectionInfo>();

			lock (m_connections)
			{
				bool existsWhiteListAreas = false;
				bool existsWhiteListServers = false;
				foreach (ConnectionInfo server in m_connections.Values)
				{
					if (server.UserList == ConnectionInfo.UserListType.Whitelist)
					{
						existsWhiteListServers = true;
						break;
					}
				}
				foreach (AreaInfo area in m_areas.Values)
				{
					if (area.UserList == AreaInfo.UserListType.Whitelist)
					{
						existsWhiteListAreas = true;
						break;
					}
				}

				foreach (ConnectionInfo server in m_connections.Values)
				{
					bool skip = false;

					if (all == false)
					{
						ConnectionInfo.UserListType serverUserList = server.UserList;
						AreaInfo.UserListType countryUserList = AreaInfo.UserListType.None;

						if (m_areas.ContainsKey(server.CountryCode))
							countryUserList = m_areas[server.CountryCode].UserList;

						if (serverUserList == ConnectionInfo.UserListType.Blacklist)
						{
							skip = true;
						}
						else if (serverUserList == ConnectionInfo.UserListType.Whitelist)
						{
							skip = false;
						}
						else
						{
							if (countryUserList == AreaInfo.UserListType.Blacklist)
							{
								skip = true;
							}
							else if ((existsWhiteListServers) && (serverUserList == ConnectionInfo.UserListType.None))
							{
								skip = true;
							}
							else if ((existsWhiteListAreas) && (countryUserList == AreaInfo.UserListType.None))
							{
								skip = true;
							}
							else if (countryUserList == AreaInfo.UserListType.Whitelist)
							{
								skip = false;
							}
						}
					}

					if (skip == false)
						list.Add(server);
				}
			}

			try
			{
				// TOFIX: in try/catch because some users report exception, "Failed to compare two elements in the array".
				list.Sort();
			}
			catch (Exception)
			{
			}

			return list;
		}

		public ConnectionInfo PickConnectionByName(string name)
		{
			lock (m_connections)
			{
				foreach (ConnectionInfo s in m_connections.Values)
				{
					if (s.DisplayName == name)
						return s;
				}

				Engine.Instance.Logs.Log(LogType.Fatal, LanguageManager.GetText("ServerByNameNotFound", name));
				return null;
			}
		}

		public ConnectionInfo PickConnection()
		{
			return PickConnection("");
		}

		public ConnectionInfo PickConnection(string preferred)
		{
			lock (m_connections)
			{
				if (preferred != "")
				{
					if (m_connections.ContainsKey(preferred))
						return m_connections[preferred];
				}

				List<ConnectionInfo> list = GetConnections(false);
				if (list.Count > 0)
					return list[0];
			}

			return null;
		}

		public void RefreshProvidersX() // Never used
		{
			// Refresh each provider
			JobsManager.ProvidersRefresh.CheckNow();
		}

		public void RefreshProvidersInvalidateConnections()
		{
			// Refresh each provider AND invalidate all ping and discovery info
			ProvidersManager.InvalidateWithNextRefresh = true;
			JobsManager.ProvidersRefresh.CheckNow();
		}

		public void InvalidatePinger()
		{
			m_jobsManager.Latency.InvalidateAll();
		}

		public void InvalidateDiscovery()
		{
			m_jobsManager.Discover.InvalidateAll();
		}

		public void InvalidateConnections()
		{
			InvalidatePinger();
			InvalidateDiscovery();

			OnRefreshUi(Core.Engine.RefreshUiMode.Full);
		}

		public void PostManifestUpdate()
		{
			OnPostManifestUpdate();

			OnRefreshUi(Core.Engine.RefreshUiMode.Full);

			if (m_networkLockManager != null)
				m_networkLockManager.OnUpdateIps();
		}

		public void RunEventCommand(string name)
		{
			if (Storage == null)
				return;

			string filename = Options.Get("event." + name + ".filename");
			string arguments = Options.Get("event." + name + ".arguments");
			bool waitEnd = Options.GetBool("event." + name + ".waitend");

			if (filename.Trim() != "")
			{
				string message = LanguageManager.GetText("AppEvent", name);
				WaitMessageSet(message, false);
				Logs.Log(LogType.Info, message);

				//Log(LogType.Verbose, "Start Running event '" + name + "', Command: '" + filename + "', Arguments: '" + arguments + "'");
				SystemExec.ExecForUserEvent(filename, arguments, waitEnd);
				//Log(LogType.Verbose, "End Running event '" + name + "', Command: '" + filename + "', Arguments: '" + arguments + "'");
			}
		}

		public XmlDocument FetchUrlXml(HttpRequest request)
		{
			HttpResponse response = FetchUrl(request);
			XmlDocument doc = new XmlDocument();
			doc.LoadXml(response.GetBodyAscii().Trim());
			return doc;
		}

		public HttpResponse FetchUrl(HttpRequest request)
		{
			if (Platform.Instance.FetchUrlInternal())
			{
				Json jRequest = request.ToJson();
				Json jResponse = Platform.Instance.FetchUrl(jRequest);
				if (jResponse.HasKey("error"))
					throw new Exception("Fetch url error:" + jResponse["error"].ValueString);
				HttpResponse response = new HttpResponse();
				response.FromJson(jResponse);
				return response;
			}
			else
			{
				Tools.Curl curl = Software.GetTool("curl") as Tools.Curl;

				return curl.Fetch(request);
			}
		}

		public int PingerInvalid()
		{
			return m_jobsManager.Latency.GetStats().Invalid;
		}

		public PingerStats PingerStats()
		{
			return m_jobsManager.Latency.GetStats();
		}

		public bool IsLogged()
		{
			if (AirVPN == null)
				return true; // TOCONTINUE

			if (AirVPN.User == null)
				return false;

			if (AirVPN.User.Attributes["login"] == null)
				return false;

			if (Options.Get("login") == "")
				return false;

			return true;
		}

		private void Auth()
		{
			if (AirVPN == null)
				return;

			Engine.Instance.WaitMessageSet(LanguageManager.GetText("AuthorizeLogin"), false);
			Logs.Log(LogType.Info, LanguageManager.GetText("AuthorizeLogin"));

			Dictionary<string, string> parameters = new Dictionary<string, string>();
			parameters["act"] = "user";

			try
			{
				XmlDocument xmlDoc = AirVPN.Fetch(LanguageManager.GetText("AuthorizeLogin"), parameters);
				string userMessage = xmlDoc.DocumentElement.GetAttributeString("message", "");

				if (userMessage != "")
				{
					Logs.Log(LogType.Fatal, userMessage);
				}
				else
				{
					Logs.Log(LogType.InfoImportant, LanguageManager.GetText("AuthorizeLoginDone"));

					AirVPN.Auth(xmlDoc.DocumentElement);

					OnCheckConnections();

					OnRefreshUi(RefreshUiMode.Full);
				}
			}
			catch (Exception ex)
			{
				Logs.Log(LogType.Fatal, LanguageManager.GetText("AuthorizeLoginFailed", ex.Message));
			}

			SaveSettings(); // 2.8

			Engine.Instance.WaitMessageClear();
		}

		private void DeAuth()
		{
			if (AirVPN == null)
				return;

			if (IsLogged())
			{
				Engine.Instance.WaitMessageSet(LanguageManager.GetText("AuthorizeLogout"), false);

				AirVPN.DeAuth();

				OnCheckConnections();

				OnRefreshUi(RefreshUiMode.Full);

				Logs.Log(LogType.InfoImportant, LanguageManager.GetText("AuthorizeLogoutDone"));

				Engine.Instance.WaitMessageClear();

				SaveSettings(); // 2.8
			}
		}

		public void ReAuth()
		{
			if (Engine.Instance.IsLogged())
			{
				DeAuth();
				Auth();
			}
		}

		public bool CanConnect()
		{
			Dictionary<string, ConnectionInfo> connections;

			lock (Engine.Connections)
				connections = new Dictionary<string, ConnectionInfo>(Engine.Connections);

			foreach (ConnectionInfo server in connections.Values)
			{
				if (server.CanConnect())
					return true;
			}

			return false;
		}

		private void SessionStart()
		{
			try
			{
				Logs.Log(LogType.Info, LanguageManager.GetText("SessionStart"));

				Engine.Instance.WaitMessageSet(LanguageManager.GetText("CheckingEnvironment"), true);

				if (CheckEnvironmentSession() == false)
				{
					WaitMessageClear();
				}
				else
				{
					if (m_threadSession != null)
						throw new Exception("Daemon already running.");

					OnSessionStart();

					m_threadSession = new Session();
				}
			}
			catch (Exception ex)
			{
				Logs.Log(LogType.Fatal, ex);

				WaitMessageClear();

				if (StartCommandLine.Get("console.mode") == "batch")
				{
					Engine.Instance.RequestStop();
				}
			}
		}

		private void SessionStop()
		{
			if (m_threadSession != null)
			{
				try
				{
					m_threadSession.SetReset("CANCEL"); // New 2.11.10
					m_threadSession.RequestStopSync();
					m_threadSession = null;
				}
				catch (Exception ex)
				{
					Logs.Log(LogType.Fatal, ex);
				}

				OnSessionStop();

				WaitMessageClear();

				Logs.Log(LogType.InfoImportant, LanguageManager.GetText("SessionStop"));
			}
		}

		public void RecomputeAreas()
		{
			// Compute areas
			lock (m_areas)
			{
				foreach (AreaInfo infoArea in m_areas.Values)
				{
					infoArea.Deleted = true;
					infoArea.Servers = 0;
					infoArea.Users = -1;
					infoArea.Bandwidth = 0;
					infoArea.BandwidthMax = 0;
				}

				List<string> areasWhiteList = Options.GetList("areas.whitelist");
				List<string> areasBlackList = Options.GetList("areas.blacklist");

				lock (m_connections)
				{
					foreach (ConnectionInfo server in m_connections.Values)
					{
						string countryCode = server.CountryCode;

						AreaInfo infoArea = null;
						if (m_areas.ContainsKey(countryCode))
						{
							infoArea = m_areas[countryCode];
							infoArea.Deleted = false;
						}

						if (infoArea == null)
						{
							// Create
							infoArea = new AreaInfo();
							infoArea.Code = countryCode;
							infoArea.Name = CountriesManager.GetNameFromCode(countryCode);
							infoArea.Deleted = false;
							m_areas[countryCode] = infoArea;
						}

						if (server.BandwidthMax != 0)
						{
							infoArea.Bandwidth += server.Bandwidth;
							infoArea.BandwidthMax += server.BandwidthMax;
						}

						if (server.Users >= 0)
						{
							if (infoArea.Users == -1)
								infoArea.Users = 0;
							infoArea.Users += server.Users;
						}

						infoArea.Servers++;
					}
				}

				for (; ; )
				{
					bool restart = false;
					foreach (AreaInfo infoArea in m_areas.Values)
					{
						if (infoArea.Deleted)
						{
							m_areas.Remove(infoArea.Code);
							restart = true;
							break;
						}
					}

					if (restart == false)
						break;
				}

				// White/black list
				foreach (AreaInfo infoArea in m_areas.Values)
				{
					string code = infoArea.Code;
					if (areasWhiteList.Contains(code))
						infoArea.UserList = AreaInfo.UserListType.Whitelist;
					else if (areasBlackList.Contains(code))
						infoArea.UserList = AreaInfo.UserListType.Blacklist;
					else
						infoArea.UserList = AreaInfo.UserListType.None;
				}
			}
		}

		public void UpdateSettings()
		{
			List<string> connectionsWhiteList = new List<string>();
			List<string> connectionsBlackList = new List<string>();
			foreach (ConnectionInfo info in m_connections.Values)
				if (info.UserList == ConnectionInfo.UserListType.Whitelist)
					connectionsWhiteList.Add(info.Code);
				else if (info.UserList == ConnectionInfo.UserListType.Blacklist)
					connectionsBlackList.Add(info.Code);
			Options.SetList("servers.whitelist", connectionsWhiteList);
			Options.SetList("servers.blacklist", connectionsBlackList);

			List<string> areasWhiteList = new List<string>();
			List<string> areasBlackList = new List<string>();
			foreach (AreaInfo info in m_areas.Values)
				if (info.UserList == AreaInfo.UserListType.Whitelist)
					areasWhiteList.Add(info.Code);
				else if (info.UserList == AreaInfo.UserListType.Blacklist)
					areasBlackList.Add(info.Code);
			Options.SetList("areas.whitelist", areasWhiteList);
			Options.SetList("areas.blacklist", areasBlackList);
		}

		public void SaveSettings()
		{
			if (m_storage != null)
			{
				UpdateSettings();

				Storage.Save();
			}
		}

		public bool CheckEnvironmentApp()
		{
			if (Platform.Instance.HasAccessToWrite(GetDataPath()) == false)
				Engine.Instance.Logs.Log(LogType.Fatal, "Unable to write in path '" + GetDataPath() + "'");

			// Local Time in the past
			if (DateTime.UtcNow < Constants.dateForPastChecking)
				Engine.Instance.Logs.Log(LogType.Fatal, LanguageManager.GetText("WarningLocalTimeInPast"));

			return Platform.Instance.OnCheckEnvironmentApp();
		}

		public bool CheckEnvironmentSession()
		{
			Software.ExceptionForRequired();

			if (Platform.Instance.HasAccessToWrite(GetDataPath()) == false)
				throw new Exception("Unable to write in path '" + GetDataPath() + "'");

			string protocol = Options.Get("mode.protocol").ToUpperInvariant();

			if ((protocol != "AUTO") && (protocol != "UDP") && (protocol != "TCP") && (protocol != "SSH") && (protocol != "SSL"))
				throw new Exception(LanguageManager.GetText("CheckingProtocolUnknown"));

			// TOCLEAN : Must be moved at provider level
			if ((protocol == "SSH") && (Software.GetTool("ssh").Available() == false))
				throw new Exception("SSH " + LanguageManager.GetText("NotFound"));

			if ((protocol == "SSL") && (Software.GetTool("ssl").Available() == false))
				throw new Exception("SSL " + LanguageManager.GetText("NotFound"));

			if (Options.GetBool("advanced.skip_alreadyrun") == false)
			{
				Dictionary<string, string> processes = Platform.Instance.GetProcessesList();

				foreach (string cmd in processes.Values)
				{
					if (cmd.StartsWith(Engine.Instance.GetOpenVpnTool().Path, StringComparison.InvariantCulture))
						throw new Exception(LanguageManager.GetText("AlreadyRunningOpenVPN", cmd));
				}
			}

			if ((Options.GetLower("proxy.mode") != "none") && (Options.GetLower("proxy.when") != "none"))
			{
				string proxyHost = Options.Get("proxy.host").Trim();
				int proxyPort = Options.GetInt("proxy.port");
				if (proxyHost == "")
					throw new Exception(LanguageManager.GetText("CheckingProxyHostMissing"));
				if ((proxyPort <= 0) || (proxyPort >= 256 * 256))
					throw new Exception(LanguageManager.GetText("CheckingProxyPortWrong"));

				if (protocol == "UDP")
					throw new Exception(LanguageManager.GetText("CheckingProxyNoUdp"));
			}

			if (Engine.Instance.Options.Get("network.ipv4.mode") == "block")
				throw new Exception(LanguageManager.GetText("CheckingIPv4BlockNotYetImplemented"));

			return Platform.Instance.OnCheckEnvironmentSession();
		}

		public IpAddresses DiscoverExit()
		{
			return m_jobsManager.Discover.DiscoverExit();
		}

		public int GetElevatedServicePort()
		{
			return Conversions.ToInt32(StartCommandLine.Get("elevated.service.port", Constants.ElevatedServicePort.ToString()));
		}

		public IpAddress GetDefaultGatewayIPv4()
		{
			if (Manifest["network_info"].Json.HasKey("ipv4-default-gateway"))
			{
				return new IpAddress(Manifest["network_info"]["ipv4-default-gateway"].Value as string);
			}
			else
				return null;
		}

		public IpAddress GetDefaultGatewayIPv6()
		{
			if (Manifest["network_info"].Json.HasKey("ipv6-default-gateway"))
			{
				return new IpAddress(Manifest["network_info"]["ipv6-default-gateway"].Value as string);
			}
			else
				return null;
		}

		public string GetDefaultInterfaceIPv4()
		{
			if (Manifest["network_info"].Json.HasKey("ipv4-default-interface"))
				return Manifest["network_info"]["ipv4-default-interface"].Value as string;
			else
				return "";
		}

		public string GetDefaultInterfaceIPv6()
		{
			if (Manifest["network_info"].Json.HasKey("ipv6-default-interface"))
				return Manifest["network_info"]["ipv6-default-interface"].Value as string;
			else
				return "";
		}

		public string GetNetworkIPv6Mode()
		{
			string mode = Engine.Instance.Options.GetLower("network.ipv6.mode");
			lock (Engine.Instance.Manifest)
			{
				bool support = Conversions.ToBool(Engine.Instance.Manifest["network_info"]["support_ipv6"].Value);
				if (support == false)
					mode = "block";
			}

			return mode;
		}

		public Json FindNetworkInterfaceInfo(string id)
		{
			foreach (Json jNetworkInterface in Manifest["network_info"]["interfaces"].Json.GetArray())
			{
				if (jNetworkInterface["id"].Value as string == id)
					return jNetworkInterface;
			}

			return null;
		}

		public Tools.ITool GetOpenVpnTool()
		{
			if (Options.GetBool("tools.hummingbird.preferred"))
			{
				Tools.ITool t = Software.GetTool("hummingbird");
				if (t.Available())
					return t;
			}

			return Software.GetTool("openvpn");
		}

		public void GenerateManifest()
		{
			UiManager.Broadcast("init.step", "message", LanguageManager.GetText("InitStepCollectSystemInfo"));

			// Data static
			Manifest = Json.Parse(Platform.Instance.FileContentsReadText(LocateResource("manifest.json")));

			lock (Manifest)
			{
				Json jAbout = new Json();
				Manifest["about"].Value = jAbout;
				jAbout["license"].Value = Platform.Instance.FileContentsReadText(LocateResource("gpl3.txt"));
				jAbout["libraries"].Value = Platform.Instance.FileContentsReadText(LocateResource("libraries.txt"));
				jAbout["thanks"].Value = Constants.Thanks;

				Json jOs = new Json();
				Manifest["os"].Value = jOs;
				jOs["code"].Value = Platform.Instance.GetSystemCode();
				jOs["name"].Value = Platform.Instance.GetName();
				jOs["mono"].Value = Platform.Instance.GetNetFrameworkVersion();

				Json jVersion = new Json();
				Manifest["version"].Value = jVersion;
				jVersion["name"].Value = Constants.Name;
				jVersion["text"].Value = GetVersionShow();
				jVersion["int"].Value = Constants.VersionInt;

				Json jUI = new Json();
				Manifest["ui"].Value = jUI;
				if (m_webserver != null)
					jUI["url"].Value = m_webserver.ListenUrl;

				Manifest["options"].Value = Options.GetJsonForManifest();

				Manifest["languages"].Value = LanguageManager.GetJsonForManifest();

				UiManager.Broadcast("init.step", "message", LanguageManager.GetText("InitStepCollectNetworkInfo"));

				Manifest["network_info"].Value = JsonNetworkInfo();
			}
		}

		public Json GenerateStatus(string logMessage)
		{
			// "full" is the text for window title.
			// "short" is the text for the menu status and tray / sysbar. If omissis, use "full".
			// Users can control the "short" behiavour, because it's invasive (big) in macOS.

			// TOFIX: "gui.osx.sysbar" are only for macOS, but can be for any platform. Rename need.

			string textFull = "";
			string textShort = "";

			Json j = new Json();
			if (logMessage != "")
			{
				// Log message
				textFull = logMessage;
				textShort = logMessage;

				if (Platform.Instance.GetCode() == "MacOS")
				{
					if ((Storage == null) || (Options.GetBool("gui.osx.sysbar.show_info") == false))
						textShort = "";
				}
			}
			else
			{
				// Connection info
				if (CurrentServer == null)
					return null;
				if (Connection == null)
					return null;

				bool showShortSpeed = true;
				bool showShortServer = true;

				if (Platform.Instance.GetCode() == "MacOS")
				{
					if (Storage != null)
					{
						showShortSpeed = Options.GetBool("gui.osx.sysbar.show_speed");
						showShortServer = Options.GetBool("gui.osx.sysbar.show_server");
					}
					else
					{
						showShortSpeed = false;
						showShortServer = false;
					}
				}

				string speedText = LanguageManager.GetText("StatusConnectedSpeed", LanguageManager.FormatBytes(Connection.BytesLastDownloadStep, true, false), LanguageManager.FormatBytes(Connection.BytesLastUploadStep, true, false));
				string serverText = CurrentServer.DisplayName;
				string country = CountriesManager.GetNameFromCode(CurrentServer.CountryCode);
				if (country != "")
					serverText += " (" + country + ")";
				if (Connection.ExitIPs.Count != 0)
					serverText += " - IP:" + Connection.ExitIPs.ToString();
				textFull = speedText + " - " + serverText;
				textShort = "";

				if (showShortSpeed)
					textShort += speedText;

				if (showShortServer)
				{
					if (textShort != "")
						textShort += " - ";
					textShort += serverText;
				}
			}

			j["full"].Value = textFull;
			if (textShort != textFull)
				j["short"].Value = textShort;

			return j;
		}

		public void RaiseStatus() // When connected
		{
			RaiseStatus("");
		}

		public void RaiseStatus(string logMessage)
		{
			Json j = GenerateStatus(logMessage);
			if (j == null)
				return;
			j["command"].Value = "ui.status";
			UiManager.Broadcast(j);
		}

		public Json JsonMainStatus()
		{
			Json j = new Json();
			j["message"].Value = m_mainStatusMessage;
			if (IsConnected())
			{
				j["app_icon"].Value = "normal";
				j["app_color"].Value = "green";
				j["action_icon"].Value = "connected";
				j["action_command"].Value = "mainaction.disconnect";
				j["action_text"].Value = LanguageManager.GetText("MainActionDisconnect");
			}
			else if (CanConnect() == false)
			{
				j["app_icon"].Value = "gray";
				j["app_color"].Value = "red";
				j["action_icon"].Value = "cantconnect";
				j["action_command"].Value = "";
				j["action_text"].Value = LanguageManager.GetText("MainActionCantConnect");
			}
			else if (IsWaiting())
			{
				j["app_icon"].Value = "gray";
				j["app_color"].Value = "yellow";
				j["action_icon"].Value = "pending";
				if ((IsWaitingCancelAllowed()) && (IsWaitingCancelPending() == false))
				{
					j["action_command"].Value = "mainaction.disconnect";
					j["action_text"].Value = LanguageManager.GetText("MainActionCancel");
				}
				else
				{
					j["action_command"].Value = "";
					j["action_text"].Value = LanguageManager.GetText("MainActionCancel");
				}
			}
			else
			{
				j["app_icon"].Value = "gray";
				j["app_color"].Value = "red";
				j["action_icon"].Value = "disconnected";
				j["action_command"].Value = "mainaction.connect";
				j["action_text"].Value = LanguageManager.GetText("MainActionConnect");
			}
			j["netlock"].Value = ((NetworkLockManager != null) && (NetworkLockManager.IsActive()));
			return j;
		}

		public void RaiseMainStatus()
		{
			Json j = JsonMainStatus();
			j["command"].Value = "ui.main-status";
			UiManager.Broadcast(j);
		}

		public Json JsonOsInfo()
		{
			// Data sended at start, rarely changes
			Json j = new Json();

			j["network_info"].Value = JsonNetworkInfo();

			Json jProvidersDefinition = new Json();
			j["providers_definitions"].Value = jProvidersDefinition;


			return j;
		}

		public Json JsonNetworkInfo()
		{
			Json jRouteList = JsonRouteList();

			Json jNetworkInfo = new Json();

			jNetworkInfo["support_ipv4"].Value = true; // Default, if unable to detect after
			jNetworkInfo["support_ipv6"].Value = true; // Default, if unable to detect after

			jNetworkInfo["routes"].Value = jRouteList;

			// Default Gateway detection
			foreach (Json jRoute in jRouteList.GetArray())
			{
				if (jRoute.HasKey("destination") == false)
					continue;
				if (jRoute.HasKey("interface") == false)
					continue;
				if (jRoute.HasKey("gateway") == false)
					continue;

				IpAddress destination = jRoute["destination"].ValueString;
				if (destination.IsDefault)
				{
					IpAddress gateway = jRoute["gateway"].ValueString;
					if (gateway.IsV4)
					{
						if (jNetworkInfo.HasKey("ipv4-default-gateway") == false)
						{
							jNetworkInfo["ipv4-default-gateway"].Value = gateway.Address;
							jNetworkInfo["ipv4-default-interface"].Value = jRoute["interface"].ValueString;
						}
					}
					else if (gateway.IsV6)
					{
						if (jNetworkInfo.HasKey("ipv6-default-gateway") == false)
						{
							jNetworkInfo["ipv6-default-gateway"].Value = gateway.Address;
							jNetworkInfo["ipv6-default-interface"].Value = jRoute["interface"].ValueString;
						}
					}
				}
			}

			Json jNetworkInterfaces = new Json();
			jNetworkInfo["interfaces"].Value = jNetworkInterfaces;
			NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
			foreach (NetworkInterface adapter in interfaces)
			{
				Json jNetworkInterface = new Json();
				jNetworkInterfaces.Append(jNetworkInterface);
				jNetworkInterface["friendly"].Value = adapter.Name;
				jNetworkInterface["id"].Value = adapter.Id.ToString();
				jNetworkInterface["name"].Value = adapter.Name;
				jNetworkInterface["description"].Value = adapter.Description;
				jNetworkInterface["type"].Value = adapter.NetworkInterfaceType.ToString();
				if (jNetworkInterface["type"].ValueString == "53") jNetworkInterface["type"].Value = "Virtual";
				jNetworkInterface["status"].Value = adapter.OperationalStatus.ToString();
				jNetworkInterface["bytes_received"].Value = adapter.GetIPv4Statistics().BytesReceived.ToString();
				jNetworkInterface["bytes_sent"].Value = adapter.GetIPv4Statistics().BytesSent.ToString();
				try
				{
					adapter.GetIPProperties().GetIPv4Properties();
					jNetworkInterface["support_ipv4"].Value = true;
				}
				catch
				{
					jNetworkInterface["support_ipv4"].Value = false;
				}
				try
				{
					adapter.GetIPProperties().GetIPv6Properties();
					jNetworkInterface["support_ipv6"].Value = true;
				}
				catch
				{
					jNetworkInterface["support_ipv6"].Value = false;
				}

				Json jNetworkInterfaceIps = new Json();
				jNetworkInterfaceIps.EnsureArray();
				jNetworkInterface["ips"].Value = jNetworkInterfaceIps;
				foreach (UnicastIPAddressInformation ip2 in adapter.GetIPProperties().UnicastAddresses)
				{
					string sIp = ip2.Address.ToString();
					IpAddress ip = new IpAddress(sIp);
					if (ip.Valid)
						jNetworkInterfaceIps.Append(ip.Address);
				}

				// If can be used for bind
				jNetworkInterface["bind"].Value = false;
				//if(adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
				if (jNetworkInterfaceIps.GetArray().Count > 0)
				{
					if (adapter.IsReceiveOnly == false)
					{
						jNetworkInterface["bind"].Value = true;
					}
				}
			}
			Platform.Instance.OnJsonNetworkInfo(jNetworkInfo);
			return jNetworkInfo;
		}

		public Json JsonRouteList()
		{
			Json jRouteList = new Json();
			jRouteList.EnsureArray();
			Platform.Instance.OnJsonRouteList(jRouteList);
			return jRouteList;
		}

		public void RaiseOsInfo()
		{
			Json j = JsonOsInfo();
			j["command"].Value = "os.info";
			UiManager.Broadcast(j);
		}

		public void UiSendQuickInfo()
		{
			// Data sended every second
			/*
			XmlItem xml = new XmlItem("command");
			xml.SetAttribute("action", "ui.info.os");

			xml.SetAttributeInt64("stats.vpn.download.bytes", Engine.ConnectedLastDownloadStep);
			xml.SetAttribute("stats.vpn.download.text", Core.Utils.FormatBytes(Engine.ConnectedLastDownloadStep, true, false));
			xml.SetAttributeInt64("stats.vpn.upload.bytes", Engine.ConnectedLastUploadStep);
			xml.SetAttribute("stats.vpn.upload.text", Core.Utils.FormatBytes(Engine.ConnectedLastUploadStep, true, false));

			Command(xml);
			*/
		}

		public void UiSendStatusInfo()
		{
			/*
			// Data sended in case of big event, like connect/disconnect.
			XmlItem xml = new XmlItem("command");
			xml.SetAttribute("action", "ui.info.status");
			xml.SetAttributeBool("waiting", IsWaiting());
			xml.SetAttribute("waiting.message", WaitMessage);
			xml.SetAttributeBool("connected", IsConnected());
			xml.SetAttributeBool("netlock", ((Engine.Instance.NetworkLockManager != null) && (Engine.Instance.NetworkLockManager.IsActive())));
			if (CurrentServer != null)
			{
				xml.SetAttribute("server.country.code", CurrentServer.CountryCode);
				xml.SetAttribute("server.display-name", CurrentServer.DisplayName);
			}
			Command(xml);
			*/
		}
	}
}

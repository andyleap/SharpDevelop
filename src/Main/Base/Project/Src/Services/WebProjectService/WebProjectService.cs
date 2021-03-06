﻿// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System;
using System.EnterpriseServices.Internal;
using System.IO;
using System.Reflection;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Dom;
using Microsoft.Win32;

namespace ICSharpCode.SharpDevelop.Project
{
	public enum IISVersion
	{
		None = 0,
		IIS5 = 5,
		IIS6,
		IIS7,
		IIS8,
		IIS_Future = 100
	}
	
	public enum WebServer
	{
		None,
		IISExpress,
		IIS
	}
	
	/// <summary>
	/// Exposes common operations used in Web Projects
	/// </summary>
	public static class WebProjectService
	{
		const string IIS_LOCATION = "Software\\Microsoft\\InetStp";
		const string IIS_MAJOR_VERSION = "MajorVersion";
		const string IIS_INSTALL_PATH = "InstallPath";
		const string DEFAULT_WEB_SITE = "Default Web Site";
		const string IIS_WEB_LOCATION = "IIS://localhost/W3SVC/1/Root";
		
		const string ASPNET_REG_PATH = @"SOFTWARE\MICROSOFT\ASP.NET";
		const string ASPNET_ROOT_VER = @"RootVer";
		
		const string FRAMEWORK_LOCATION = @"%systemroot%\Microsoft.NET\";
		const string FRAMEWORK32 = @"Framework\";
		const string FRAMEWORK64 = @"Framework64\";
		
		/// <summary>
		/// Gets &quot;iisexpress&quot; string.
		/// </summary>
		public const string IIS_EXPRESS_PROCESS_NAME = "iisexpress";
		
		/// <summary>
		/// Gets &quot;aspnet_wp&quot; string.
		/// </summary>
		public const string IIS_5_PROCESS_NAME = "aspnet_wp";
		
		/// <summary>
		/// Gets &quot;w3wp&quot; string.
		/// </summary>
		public const string IIS_NEW_PROCESS_NAME = "w3wp";
		
		/// <summary>
		/// Gets IIS Express process location.
		/// </summary>
		public static string IISExpressProcessLocation {
			get {
				return Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles) +
					@"\IIS Express\iisexpress.exe";
			}
		}
		
		/// <summary>
		/// Gets the IIS worker process name.
		/// </summary>
		public static string GetWorkerProcessName(WebServer webServer)
		{
			if (webServer == WebServer.IISExpress) {
				return GetIISExpressWorkerProcessName();
			}
			return GetIISWorkerProcessName();
		}
		
		public static string GetIISExpressWorkerProcessName()
		{
			if (!IsIISExpressInstalled)
				return ResourceService.GetString("ICSharpCode.WepProjectOptionsPanel.IISNotFound");
			
			return IIS_EXPRESS_PROCESS_NAME;
		}
		
		public static string GetIISWorkerProcessName()
		{
			if (!IsIISInstalled)
				return ResourceService.GetString("ICSharpCode.WepProjectOptionsPanel.IISNotFound");
			
			try {
				switch (IISVersion)
				{
					case IISVersion.IIS5:
						return IIS_5_PROCESS_NAME;
					default:
						return IIS_NEW_PROCESS_NAME;
				}
			}
			catch (Exception ex) {
				return ex.Message;
			}
		}
		
		public static string GetWorkerProcessLocation(WebServer webServer)
		{
			if (webServer == WebServer.IISExpress) {
				return GetIISExpressWorkerProcessLocation();
			}
			return GetIISWorkerProcessLocation();
		}
		
		public static string GetIISExpressWorkerProcessLocation()
		{
			if (!IsIISExpressInstalled)
				return ResourceService.GetString("ICSharpCode.WepProjectOptionsPanel.IISNotFound");
					
			return GetDefaultIISWorkerProcessLocation();
		}
		
		public static string GetIISWorkerProcessLocation()
		{
			if (!IsIISInstalled)
				return ResourceService.GetString("ICSharpCode.WepProjectOptionsPanel.IISNotFound");
			
			try {
				if (IISVersion == IISVersion.IIS5) {
					return GetIIS5WorkerProcessLocation();
				}
				return GetDefaultIISWorkerProcessLocation();
			}
			catch (Exception ex) {
				return ex.Message;
			}
		}
		
		public static string GetIIS5WorkerProcessLocation()
		{
			string location = FRAMEWORK_LOCATION + (Environment.Is64BitOperatingSystem ? FRAMEWORK64 : FRAMEWORK32);
			
			string frameworkString = "";
			
			RegistryService.GetRegistryValue<string>(
				RegistryHive.LocalMachine,
				ASPNET_REG_PATH,
				ASPNET_ROOT_VER,
				RegistryValueKind.String,
				out frameworkString);
			int ind = frameworkString.LastIndexOf('.');
			location += "v" + frameworkString.Substring(0, ind) + "\\";
			return location;
		}
		
		public static string GetDefaultIISWorkerProcessLocation()
		{
			string regValue = "";
			
			RegistryService.GetRegistryValue<string>(
				RegistryHive.LocalMachine,
				IIS_LOCATION,
				IIS_INSTALL_PATH,
				RegistryValueKind.String,
				out regValue);
			return regValue + "\\";
		}
		
		/// <summary>
		/// Gets a value representing whether IIS is installed.
		/// </summary>
		public static bool IsIISInstalled {
			get { return (int)IISVersion >= 4; }
		}
		
		public static bool IsIISExpressInstalled {
			get { return File.Exists(IISExpressProcessLocation); }
		}
		
		public static bool IsIISOrIISExpressInstalled {
			get { return IsIISInstalled || IsIISExpressInstalled; }
		}
		
		/// <summary>
		/// Gets a value representing IIS version.
		/// </summary>
		public static IISVersion IISVersion
		{
			get {
				int regValue = 0;
				
				RegistryService.GetRegistryValue<int>(
					RegistryHive.LocalMachine,
					IIS_LOCATION,
					IIS_MAJOR_VERSION,
					RegistryValueKind.DWord,
					out regValue);
				
				if (regValue > 4)
					return (IISVersion)regValue;
				
				return IISVersion.None;
			}
		}
		
		/// <summary>
		/// Creates a virtual directory in local IIS or IIS Express.
		/// </summary>
		/// <param name="virtualDirectoryName">Virtual directory name.</param>
		/// <param name="virtualDirectoryPath">Physical path.</param>
		/// <returns>Error string or string null = no errors.</returns>
		public static string CreateVirtualDirectory(WebServer webServer, string virtualDirectoryName, string physicalDirectoryPath)
		{
			try {
				string iisNotFoundError = ResourceService.GetString("ICSharpCode.WepProjectOptionsPanel.IISNotFound");
				if (!IsIISOrIISExpressInstalled)
					return iisNotFoundError;
				
				string error;
				
				switch(IISVersion)
				{
					case IISVersion.IIS5:
					case IISVersion.IIS6:
						var vr = new IISVirtualRoot();
						vr.Create(IIS_WEB_LOCATION,
						          physicalDirectoryPath,
						          virtualDirectoryName,
						          out error);
						break;
					default:
						if (!IsIISExpressInstalled && (IISVersion == IISVersion.None))
							return iisNotFoundError;

						// TODO: find a better way to create IIS applications without Microsoft.Web.Administration.ServerManager
						string name = "/" + virtualDirectoryName;
						// load from GAC
						Assembly webAdministrationAssembly = null;
						try {
							// iis installed
							foreach(DomAssemblyName assembly in GacInterop.GetAssemblyList()) {
								if (assembly.FullName.Contains("Microsoft.Web.Administration")) {
									if (IsAssemblyForWebServer(webServer, assembly)) {
										webAdministrationAssembly = Assembly.Load(assembly.FullName);
										break;
									}
								}
							}
						} catch {
							return iisNotFoundError;
						}
						if (webAdministrationAssembly == null)
							return iisNotFoundError;
						
						// use dynamic because classic reflection is way TOO ugly
						dynamic manager = webAdministrationAssembly.CreateInstance("Microsoft.Web.Administration.ServerManager");
						
						if (manager.Sites[DEFAULT_WEB_SITE] != null) {
							if (manager.Sites[DEFAULT_WEB_SITE].Applications[name] == null) {
								manager.Sites[DEFAULT_WEB_SITE].Applications.Add(name, physicalDirectoryPath);
								manager.CommitChanges();
								error = string.Empty;
							} else {
								error = ResourceService.GetString("ICSharpCode.WepProjectOptionsPanel.ApplicationExists");
							}
						} else {
							if (manager.Sites[0].Applications[name] == null) {
								manager.Sites[0].Applications.Add(name, physicalDirectoryPath);
								manager.CommitChanges();
								error = string.Empty;
							} else {
								error = ResourceService.GetString("ICSharpCode.WepProjectOptionsPanel.ApplicationExists");
							}
						}
						manager.Dispose();
						break;
				}
				
				return error;
			}
			catch (Exception ex) {
				return ex.Message;
			}
		}
		
		static bool IsAssemblyForWebServer(WebServer webServer, DomAssemblyName assembly)
		{
			if (webServer == WebServer.IISExpress) {
				return (assembly.Version.Major == 7) && (assembly.Version.Minor == 9);
			}
			return true;
		}
	}
}

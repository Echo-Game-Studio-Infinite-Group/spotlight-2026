/*******************************************************************************
The content of this file includes portions of the proprietary AUDIOKINETIC Wwise
Technology released in source code form as part of the game integration package.
The content of this file may not be used without valid licenses to the
AUDIOKINETIC Wwise Technology.
Note that the use of the game engine is subject to the Unity(R) Terms of
Service at https://unity3d.com/legal/terms-of-service
 
License Usage
 
Licensees holding valid licenses to the AUDIOKINETIC Wwise Technology may use
this file in accordance with the end user license agreement provided with the
software or, alternatively, in accordance with the terms contained
in a written agreement between you and Audiokinetic Inc.
Copyright (c) 2026 Audiokinetic Inc.
*******************************************************************************/

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using AK.Wwise.Unity.Logging;

[InitializeOnLoad]
public class AkPluginActivator : UnityEditor.AssetPostprocessor
{
	private const string EditorConfiguration = AkPluginActivatorConstants.CONFIG_PROFILE;
	private static bool bIsAlreadyActivating = false;
	static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths, bool didDomainReload)
	{
		if (UnityEditor.AssetDatabase.IsAssetImportWorkerProcess() || bIsAlreadyActivating)
		{
			return;
		}

		if (didDomainReload)
		{
			ActivatePluginsForEditor();	
		}
	}

	public static string GetCurrentConfig()
	{
		var CurrentConfig = AkWwiseProjectInfo.GetData().CurrentPluginConfig;
		if (string.IsNullOrEmpty(CurrentConfig))
		{
			CurrentConfig = AkPluginActivatorConstants.CONFIG_PROFILE;
		}
		
		return CurrentConfig;
	}

	public static Dictionary<BuildTarget, AkPlatformPluginActivator> BuildTargetToPlatformPluginActivator = new Dictionary<BuildTarget, AkPlatformPluginActivator>();

	public static void RegisterPlatformPluginActivator(BuildTarget target, AkPlatformPluginActivator platformPluginActivator)
	{
		WwiseLogger.LogFormat(LogLevel.Verbose, "Adding platform {0} to PluginActivator", target.ToString());
		BuildTargetToPlatformPluginActivator.Add(target, platformPluginActivator);
	}

	internal static string GetPluginInfoPlatform(string path)
	{
		var indexOfPluginFolder = path.IndexOf(AkPluginActivatorConstants.WwisePluginFolder, StringComparison.OrdinalIgnoreCase);
		if (indexOfPluginFolder == -1)
		{
			return null;
		}

		return path.Substring(indexOfPluginFolder + AkPluginActivatorConstants.WwisePluginFolder.Length + 1).Split('/')[0];
	}

	internal static List<PluginImporter> GetWwisePluginImporters(string platformFilter = "")
	{
		PluginImporter[] pluginImporters = PluginImporter.GetAllImporters();
		List<PluginImporter> wwisePlugins = new List<PluginImporter>();
		foreach (var pluginImporter in pluginImporters)
		{
			if (pluginImporter.assetPath.Contains("Wwise/API/"))
			{
				if (string.IsNullOrEmpty(platformFilter) || platformFilter == GetPluginInfoPlatform(pluginImporter.assetPath))
				{
					wwisePlugins.Add(pluginImporter);
				}
			}
		}
		return wwisePlugins;
	}

	public class PluginImporterInformation
	{
		public string PluginName;
		public string PluginArch;
		public string PluginSDKVersion;
		public string PluginConfig;

		public string EditorOS;
		public string EditorCPU;

		public bool IsX86 => PluginArch == "x86";
		public bool IsX64 => PluginArch == "x86_64";

		public bool IsSupportLibrary => AkPluginActivatorConstants.SupportLibraries.Contains(PluginName);
	}
	
	public static void ActivatePluginsForDeployment(BuildTarget target, bool Activate)
	{
		if (!BuildTargetToPlatformPluginActivator.TryGetValue(target, out var platformPluginActivator))
		{
			WwiseLogger.LogFormat(LogLevel.Error, "Unable to find Plugin Activator for Build Target {0}. Check that platform {1} has been installed as part of your Wwise Integration.", target, target);
			return;
		}

		if (!platformPluginActivator.IsBuildEnvironmentValid())
		{
			WwiseLogger.LogFormat(LogLevel.Error, "Build Environment for platform {0} is not valid. Current BuildTarget is {1}", platformPluginActivator.WwisePlatformName, EditorUserBuildSettings.activeBuildTarget);
			return;
		}

		bIsAlreadyActivating = true;

		if (Activate)
		{
			StaticPluginRegistration.Setup(target, platformPluginActivator);
		}

		var importers = GetWwisePluginImporters();
		var assetChanged = false;
		AssetDatabase.StartAssetEditing();
		foreach (var pluginImporter in importers)
		{
			if (pluginImporter.GetCompatibleWithAnyPlatform())
			{
				WwiseLogger.LogFormat(LogLevel.Log, "Plugin{0} was compatible with the \"any\" platform, deactivating.", pluginImporter.assetPath);
				pluginImporter.SetCompatibleWithAnyPlatform(false);
				assetChanged = true;
			}

			var pluginPlatform = GetPluginInfoPlatform(pluginImporter.assetPath);
			if (pluginPlatform != platformPluginActivator.PluginDirectoryName)
			{
				if (Activate)
				{
					platformPluginActivator.FilterOutPlatformIfNeeded(target, pluginImporter, pluginPlatform);
				}

				continue;
			}

			var pluginInfo = platformPluginActivator.GetPluginImporterInformation(pluginImporter);
			var bShouldActivatePlugin = platformPluginActivator.ConfigurePlugin(pluginImporter, pluginInfo);

			if (pluginInfo.PluginConfig == "DSP")
			{
				if (!pluginInfo.IsSupportLibrary && !AkPlatformPluginList.IsPluginUsed(platformPluginActivator, pluginPlatform, Path.GetFileNameWithoutExtension(pluginImporter.assetPath)))
				{
					WwiseLogger.LogFormat(LogLevel.Verbose, "Plugin{0} is not used, skipping.", pluginImporter.assetPath);
					bShouldActivatePlugin = false;
				}
			}
			else if (pluginInfo.PluginConfig != GetCurrentConfig())
			{
				WwiseLogger.LogFormat(LogLevel.Verbose, "Plugin{0} does not match current config ({1}). Skipping.", pluginImporter.assetPath, GetCurrentConfig());
				bShouldActivatePlugin = false;
			}

			if (!string.IsNullOrEmpty(pluginInfo.PluginSDKVersion))
			{
				var sdkCompatible = platformPluginActivator.IsPluginSDKVersionCompatible(pluginInfo.PluginSDKVersion);
				WwiseLogger.LogFormat(LogLevel.Verbose, "Plugin {0} is {1}compatible with current platform SDK", pluginImporter.assetPath, (sdkCompatible ? "" : "NOT "));
				bShouldActivatePlugin &= sdkCompatible;
			}

			bool isCompatibleWithPlatform = bShouldActivatePlugin && Activate;
			WwiseLogger.LogFormat(LogLevel.Verbose, "Will set plugin {0} as {1}compatible with platform.", pluginImporter.assetPath, (isCompatibleWithPlatform ? "" : "NOT "));
			assetChanged |= pluginImporter.GetCompatibleWithPlatform(target) != isCompatibleWithPlatform;

			pluginImporter.SetCompatibleWithPlatform(target, isCompatibleWithPlatform);

			if (assetChanged)
			{
				WwiseLogger.LogFormat(LogLevel.Verbose, "Changed plugin {0}, saving and reimporting.", pluginImporter.assetPath);
				pluginImporter.SaveAndReimport();
			}
		}
		AssetDatabase.StopAssetEditing();
		bIsAlreadyActivating = false;
	}

	public static void ActivatePluginsForEditor()
	{
		var importers = GetWwisePluginImporters();
		var changedSomeAssets = false;

		bIsAlreadyActivating = true;
		AssetDatabase.StartAssetEditing();
		foreach (var pluginImporter in importers)
		{
			var pluginPlatform = GetPluginInfoPlatform(pluginImporter.assetPath);
			if (string.IsNullOrEmpty(pluginPlatform) || (pluginPlatform != "Mac" && pluginPlatform != "Windows" && pluginPlatform != "Linux"))
			{
				pluginImporter.SetCompatibleWithEditor(false);
				changedSomeAssets = true;
				continue;
			}

			BuildTarget pluginBuildTarget;
			switch (pluginPlatform)
			{
				case "Windows":
					pluginBuildTarget = BuildTarget.StandaloneWindows64;
					break;
				case "Mac":
					pluginBuildTarget = BuildTarget.StandaloneOSX;
					break;
				case "Linux":
					pluginBuildTarget = BuildTarget.StandaloneLinux64;
					break;
				default:
					pluginBuildTarget = BuildTarget.StandaloneWindows64;
					break;
			}
			
			if (!BuildTargetToPlatformPluginActivator.TryGetValue(pluginBuildTarget, out var platformPluginActivator))
			{
				WwiseLogger.LogFormat(LogLevel.Log, "Build Target {0} not supported.", pluginBuildTarget);
				bIsAlreadyActivating = false;
				AssetDatabase.StopAssetEditing();
				return;
			}

			var pluginInfo = platformPluginActivator.GetPluginImporterInformation(pluginImporter);
			
			var assetChanged = false;
			if (pluginImporter.GetCompatibleWithAnyPlatform())
			{
				WwiseLogger.LogFormat(LogLevel.Verbose, "ActivatePluginsForEditor: Plugin{0} was compatible with the \"any\" platform, deactivating.", pluginImporter.assetPath);
				pluginImporter.SetCompatibleWithAnyPlatform(false);
				assetChanged = true;
			}

			var bActivate = false;
			if (!string.IsNullOrEmpty(pluginInfo.EditorOS))
			{
				if (pluginInfo.PluginConfig == "DSP")
				{
					if (!AkPlatformPluginList.ContainsPlatform(platformPluginActivator.WwisePlatformName))
					{
						continue;
					}

					bActivate = AkPlatformPluginList.IsPluginUsed(platformPluginActivator, pluginPlatform,
						Path.GetFileNameWithoutExtension(pluginImporter.assetPath));
				}
				else
				{
					bActivate = pluginInfo.PluginConfig == EditorConfiguration;
				}

				if (bActivate)
				{
					WwiseLogger.LogFormat(LogLevel.Verbose, "ActivatePluginsForEditor: Activating {0}", pluginImporter.assetPath);
					pluginImporter.SetEditorData("CPU", pluginInfo.EditorCPU);
					pluginImporter.SetEditorData("OS", pluginInfo.EditorOS);
				}

				assetChanged |= pluginImporter.GetCompatibleWithEditor() != bActivate;
				pluginImporter.SetCompatibleWithEditor(bActivate);
			}
			else
			{
				WwiseLogger.LogFormat(LogLevel.Verbose, "ActivatePluginsForEditor: Could not determine EditorOS for {0}", pluginImporter.assetPath);
			}

			if (assetChanged)
			{
				changedSomeAssets = true;
				WwiseLogger.LogFormat(LogLevel.Verbose, "ActivatePluginsForEditor: Changed plugin {0}, saving and reimporting.", pluginImporter.assetPath);
			}
		}
		
		AssetDatabase.StopAssetEditing();
		if (changedSomeAssets)
		{
			WwiseLogger.LogFormat(LogLevel.Log, "Plugins successfully activated for {0} in Editor.", EditorConfiguration);
			AssetDatabase.Refresh();
		}

		bIsAlreadyActivating = false;
	}

	public static void DeactivateAllPlugins()
	{
		var importers = GetWwisePluginImporters();
		foreach (var pluginImporter in importers)
		{
			if (pluginImporter.assetPath.IndexOf(AkPluginActivatorConstants.WwisePluginFolder, StringComparison.OrdinalIgnoreCase) == -1)
			{
				continue;
			}

			pluginImporter.SetCompatibleWithAnyPlatform(false);
			pluginImporter.SaveAndReimport();
		}
	}

	public static void ForceUpdate()
	{
		AkPlatformPluginList.Update(true);
		Update();
	}

	public static void Update()
	{
		AkPluginActivatorMenus.CheckMenuItems(GetCurrentConfig());
	}

}
#endif

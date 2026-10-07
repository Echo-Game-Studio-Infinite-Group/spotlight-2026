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
using System;
using UnityEditor;
using UnityEditor.PackageManager.Requests;
using UnityEditor.PackageManager;
using UnityEngine;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor.Callbacks;
using AK.Wwise.Unity.Logging;
using LogLevel = AK.Wwise.Unity.Logging.LogLevel;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

#if AK_WWISE_ADDRESSABLES
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using AK.Wwise.Unity.WwiseAddressables;
#endif

[InitializeOnLoad]
public static class AddressableInstaller
{
    static AddressableInstaller()
    {
        // Subscribe to the event at editor load time
        Events.registeredPackages += OnPackagesRegistered;
    }

    private static void OnPackagesRegistered(PackageRegistrationEventArgs args)
    {
#if AK_WWISE_ADDRESSABLES && UNITY_ADDRESSABLES
        if (AkWwiseEditorSettings.Instance.InstallationWasRequested)
        {
            foreach (var package in args.added)
            {
                if (package.assetPath == "Packages/com.audiokinetic.wwise.addressables")
                {
                    AkWwiseEditorSettings.Instance.InstallationWasRequested = false;
                    AkWwiseEditorSettings.Instance.SaveSettings();
                    AddressableSetup();
                }
            }
        }
#else
        foreach (var package in args.removed)
        {
            if (package.assetPath == "Packages/com.audiokinetic.wwise.addressables")
            {
                CompleteUninstallation();
            }
        }
#endif
    }
    
    private static string Name = nameof(AddressableInstaller);
    
    // Local value of the settings used to determine whether to use command line arguments or settings from the xml file.
    private static string _addressableBankFolder = "";
#if AK_WWISE_ADDRESSABLES && UNITY_ADDRESSABLES
    private static bool _automaticallyUpdateExternalSourcesPath = false;
    private static string _externalSourcesPath = "";
    private static bool _enableUninstallationPrompt = true;
    private static bool _disableAsynchronousBankLoading = true;
#else
    private static string _packageSource = "";
#endif
    
    private static void ReadSettings()
    {
        string[] args = System.Environment.GetCommandLineArgs();

        _addressableBankFolder = AkWwiseEditorSettings.Instance.AddressableBankFolder;
        if (args.Contains("-addressableBankFolder"))
        {
            int index = Array.IndexOf(args, "-addressableBankFolder");
            _addressableBankFolder = args[index + 1];
        }
#if AK_WWISE_ADDRESSABLES && UNITY_ADDRESSABLES
        _automaticallyUpdateExternalSourcesPath = AkWwiseEditorSettings.Instance.AutomaticallyUpdateExternalSourcesPath;
        if (args.Contains("-updateExternalSourcesPath"))
        {
            int index = Array.IndexOf(args, "-updateExternalSourcesPath");
            _automaticallyUpdateExternalSourcesPath = args[index + 1] == "true";
        }
        _externalSourcesPath = AkWwiseEditorSettings.Instance.ExternalSourcesPath;
        if (args.Contains("-externalSourcesPath"))
        {
            int index = Array.IndexOf(args, "-externalSourcesPath");
            _externalSourcesPath = args[index + 1];
            //updateExternalSourcesPath would be optional if externalSourcesPath is provided
            _automaticallyUpdateExternalSourcesPath = true;
        }
        
        // AddressableAssetBuilder Settings
        bool useCustomBuildScript = AkWwiseEditorSettings.Instance.UseCustomBuildScript;
        if (args.Contains("-useCustomBuildScript"))
        {
            int index = Array.IndexOf(args, "-useCustomBuildScript");
            _externalSourcesPath = args[index + 1];
        }
        string addressableAssetBuilderPath = AkWwiseEditorSettings.Instance.AddressableAssetBuilderPath;
        if (args.Contains("-addressableAssetBuilderPath"))
        {
            int index = Array.IndexOf(args, "-addressableAssetBuilderPath");
            _externalSourcesPath = args[index + 1];
            //useCustomBuildScript would be optional if addressableAssetBuilderPath is provided
            useCustomBuildScript = true;
        }
        AddressableAssetBuilder.ApplySettings(useCustomBuildScript, addressableAssetBuilderPath);
        _enableUninstallationPrompt = AkWwiseEditorSettings.Instance.EnableUninstallationPrompt;
        if (args.Contains("-disableUninstallationPrompt"))
        {
            int index = Array.IndexOf(args, "-disableUninstallationPrompt");
            _enableUninstallationPrompt = args[index + 1] == "true";
        } 
        _disableAsynchronousBankLoading = AkWwiseEditorSettings.Instance.DisableAsynchronousBankLoading;
        if (args.Contains("-disableAsynchronousBankLoadingOnUninstallation"))
        {
            int index = Array.IndexOf(args, "-disableAsynchronousBankLoadingOnUninstallation");
            _disableAsynchronousBankLoading = args[index + 1] == "true";
        }
#else
        _packageSource = AkWwiseEditorSettings.Instance.UseGitRepository ? WwiseSettings.GitRepositoryLink : AkWwiseEditorSettings.Instance.PackageSource;
        if (args.Contains("-packageSource"))
        {
            int index = Array.IndexOf(args, "-packageSource");
            _packageSource = args[index + 1];
        }
#endif
    }
    
    /// <summary>
    /// Toogle the AkInitializer On or Off
    /// </summary>
    /// <param name="value">The value at which the AkInitializer enabled will be set at.</param>
    private static void ToggleAkInitializer(bool value)
    {
        GameObject targetObject = GameObject.Find("WwiseGlobal");
        if (targetObject != null)
        {
            AkInitializer script = targetObject.GetComponent<AkInitializer>();
            if (script != null)
            {
                script.enabled = value; 
            }
        }
    }
    
    /// <summary>
    /// Simple Debug Log formatter
    /// </summary>
    /// <param name="source">The source of the log</param>
    /// <param name="errorMsg">The message to log</param>
    public static void LogError(string source, string errorMsg)
    {
        WwiseLogger.LogFormat(LogLevel.Error, "{0}: {1}", source, errorMsg);
    }

    public static void LogWarning(string source, string errorMsg)
    {
        WwiseLogger.LogFormat(LogLevel.Warning, "{0}: {1}", source, errorMsg);
    }

    public static void Log(string source, string errorMsg)
    {
        WwiseLogger.LogFormat(LogLevel.Log, "{0}: {1}", source, errorMsg);
    }
    private static AddRequest _addRequest;

#if !AK_WWISE_ADDRESSABLES
    /// <summary>
    /// Menu item to install the Wwise Addressable Package and set it up
    /// </summary>
    [MenuItem("Wwise/Install Addressables")]
    public static void InstallPackage()
    {
        ReadSettings();
        AkWwiseEditorSettings.Instance.InstallationWasRequested = true;
        AkWwiseEditorSettings.Instance.SaveSettings();
        var installationPathIsEmpty = string.IsNullOrEmpty(AkWwiseEditorSettings.Instance.WwiseInstallationPath);
        var waapiPortIsEmpty = string.IsNullOrEmpty(AkWwiseEditorSettings.Instance.WaapiPort);
        var waapiIPIsEmpty = string.IsNullOrEmpty(AkWwiseEditorSettings.Instance.WaapiIP);
        if (installationPathIsEmpty || waapiPortIsEmpty || waapiIPIsEmpty)
        {
            string missingSettings = (installationPathIsEmpty ? "WwiseApplicationPath, " : string.Empty) + (waapiPortIsEmpty ? "Waapi Port, " : string.Empty) + (waapiIPIsEmpty ? "Waapi IP, " : string.Empty);
            missingSettings = missingSettings.TrimEnd(new char[] { ',', ' ' });
            
            EditorUtility.DisplayDialog("Wwise Addressables installation aborted", $"The installation process was aborted as the following setting(s) were not set:\n{missingSettings}.\nAdjust your Wwise integration settings and try again.", "OK");
            return;
        }
        AdjustLoadBankAsynchronously();
        if (!InstallAddressablePackage(_packageSource))
        {
            LogError(Name, $"Failed to install the Wwise Unity Addressable package from {_packageSource}");
            return;
        }
    }
#else
    /// <summary>
    /// Menu item to uninstall the Wwise Addressable Package and clean up the Unity project
    /// </summary>
    [MenuItem("Wwise/Uninstall Addressables")]
    public static void UninstallPackage()
    {
        ReadSettings();
        if (!AdjustLoadBankAsynchronously())
        {
            return;
        }
        ToggleAkInitializer(false);
        AkSoundEngineController.Instance.DisableEditorLateUpdate();
        EditorApplication.update += ContinueUninstallation;
    }
    
    /// <summary>
    /// Continue the uninstallation process after disabling Wwise components interacting with the Wwise Addressables Package.
    /// </summary>
    private static void ContinueUninstallation()
    {
        ReadSettings();
        //Clean the addressables assets being used.
        CleanupAddressables();
        //Remove the init bank holder (Wwise Addressables only asset)
        RemoveInitBankHolder();
        //Delete the Wwise Addressables Assets under Unity Application data path
        DeleteWwiseAddressablesBankFolder();
        //Delete the Wwise Addressable Groups
        DeleteWwiseAddressableGroups();
        //Remove the Wwise Addresables Package
        RemoveWwiseAddressablePackage();
        EditorApplication.update -= ContinueUninstallation;
    }
    
#endif

    private static bool AdjustLoadBankAsynchronously()
    {
#if AK_WWISE_ADDRESSABLES
        bool shouldDisableAsynchronousBankLoading = false;
        if (_enableUninstallationPrompt)
        {
            int dialogChoiceIndex = EditorUtility.DisplayDialogComplex(
                "Wwise Unity Addressables Uninstallation",
                "Set the Load Bank Asynchronous settings to false during uninstallation? The original value was modified during the installation of the Wwise Unity Addressables integration package. In most projects, it is disabled when not working with Addressables. You can set a default value for this prompt or disable it in the Wwise Settings.",
                "Yes",
                "Cancel",
                "No"
            );

            if (dialogChoiceIndex == 1)
            {
                return false;
            }
            shouldDisableAsynchronousBankLoading = dialogChoiceIndex == 0;
        }
        else
        {
            shouldDisableAsynchronousBankLoading = _disableAsynchronousBankLoading;
        }

        if (shouldDisableAsynchronousBankLoading)
        {
            var platformSettings = AkWwiseInitializationSettings.GetAllPlatformSettings();
            foreach (var platformSetting in platformSettings)
            {
                platformSetting.LoadBanksAsynchronously = false;
                EditorUtility.SetDirty(platformSetting);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
        return true;
#else

        var platformSettings = AkWwiseInitializationSettings.Instance.PlatformSettingsList;
        foreach (var platformSetting in platformSettings)
        {
            if (platformSetting)
            {
                platformSetting.LoadBanksAsynchronously = true;
                EditorUtility.SetDirty(platformSetting);
            }
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        return true;
#endif
    }
  
#if AK_WWISE_ADDRESSABLES && UNITY_ADDRESSABLES
    /// <summary>
    /// Setup the Wwise Addressable Package
    /// </summary>
    private static void AddressableSetup()
    {
        ReadSettings();
        if (!AddressableBankPathSetter.SetBankPath(_addressableBankFolder))
        {
            LogError(Name,  $"Failed to set the bank folder to {_addressableBankFolder}");;
            return;
        }

        if (_automaticallyUpdateExternalSourcesPath)
        {
            if (!AddressableBankPathSetter.SetExternalSourcePath(_externalSourcesPath))
            {
                LogError(Name,  $"Failed to set the bank folder to {_externalSourcesPath}");;
                return;
            }
        }

        if (!AkUtilities.IsSoundbankGenerationAvailable(AkWwiseEditorSettings.Instance.WwiseInstallationPath))
        {
            LogWarning(Name,$"Sound bank generation is not available. Make sure that the Wwise Project is opened in the Wwise Authoring and please edit the Wwise Settings to ensure the the ip and port are set properly. If it's still not working, ensure that the Wwise Application Path is properly set.");;
        }
        AkUtilities.GenerateSoundbanks(AkWwiseEditorSettings.Instance.WwiseInstallationPath, AkWwiseEditorSettings.WwiseProjectAbsolutePath);
        AddressableAssetBuilder.AddressableSetup();
        SceneUpdater.ReloadAllScenes();
    }
#else
    /// <summary>
    /// Complete the Wwise Addressable Package uninstallation process after the package removal.
    /// </summary>
    private static void CompleteUninstallation()
    {
        ReadSettings();
        //Reset the Root output Path and SoundBank Paths to asset the Unity Application Data Path.
        var settings = AkWwiseEditorSettings.Instance;
        AddressableBankPathSetter.SetSoundbankPath(settings.SoundbankPath);
        AddressableBankPathSetter.SetExternalSourcePath("GeneratedSoundBanks");

        //Regenerate the soundbanks
        string wwiseInstallationPath = "";
#if UNITY_EDITOR_WIN
        wwiseInstallationPath = AkWwiseEditorSettings.Instance.WwiseInstallationPathWindows;
#elif UNITY_EDITOR_OSX
		wwiseInstallationPath = AkWwiseEditorSettings.Instance.WwiseInstallationPathMac;
#endif
        if (!AkUtilities.IsSoundbankGenerationAvailable(wwiseInstallationPath))
        {
            LogWarning(Name,$"Sound bank generation is not available. Make sure that the Wwise Project is opened in the Wwise Authoring and please edit the Wwise Settings to ensure the the ip and port are set properly. If it's still not working, ensure that the Wwise Application Path is properly set.");;
        }
        else
        {
            AkUtilities.GenerateSoundbanks(wwiseInstallationPath, AkWwiseEditorSettings.WwiseProjectAbsolutePath);
        }
        //Re-enable the SoundEngineController LateUpdate and the AkInitializer that were disabled during the uninstallation process.
        AkSoundEngineController.Instance.EnableEditorLateUpdate();
        ToggleAkInitializer(true);
        if (UnityEditorInternal.InternalEditorUtility.inBatchMode)
        {
            EditorApplication.Exit(0);
        }
    }
#endif
    /// <summary>
    /// Installs the Wwise Addressable Package
    /// </summary>
    /// <param name="packageSource">The source of the package. Can either be a local path or a git url</param>
    private static bool InstallAddressablePackage(string packageSource)
    {
        if (string.IsNullOrEmpty(packageSource))
        {
            WwiseLogger.Error("Package source not provided. Use -packageSource=<url_or_path> in the command line.");
            return false;
        }
        
        packageSource = packageSource.Replace('\\', '/');

        if (IsGitUrl(packageSource))
        {
            InstallFromUrl(packageSource);
        }
        else if (Directory.Exists(packageSource))
        {
            InstallFromLocalPath(packageSource);
        }
        else
        {
            WwiseLogger.LogFormat(LogLevel.Error, "Invalid package source: {0}. Ensure it is a valid Git URL or local path.", packageSource);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Installs a Unity package from a Git URL.
    /// </summary>
    /// <param name="url">The Git URL of the package.</param>
    private static void InstallFromUrl(string url)
    {
        WwiseLogger.LogFormat(LogLevel.Log, "Installing package from Git URL: {0}", url);
        _addRequest = Client.Add(url);
        EditorApplication.update += Progress;
    }

    /// <summary>
    /// Installs a Unity package from a local path.
    /// </summary>
    /// <param name="path">The local path of the package.</param>
    private static void InstallFromLocalPath(string path)
    {
        // Replace backslashes with forward slashes
        string pathUri = path.Replace("\\", "/");

        // Prepend "file:" only if not already present
        if (!pathUri.StartsWith("file:"))
        {
            pathUri = $"file:{pathUri}";
        }
        _addRequest = Client.Add(pathUri);
        EditorApplication.update += Progress;
    }
    
    /// <summary>
    /// Extracts the package name from a package.json file content.
    /// </summary>
    /// <param name="packageJson">The content of the package json file.</param>
    private static string ExtractPackageName(string packageJson)
    {
        var match = Regex.Match(packageJson, "\"name\": \"(?<name>.*?)\"");
        return match.Success ? match.Groups["name"].Value : null;
    }

    /// <summary>
    /// Utility to check if a string is a Git URL.
    /// </summary>
    /// <param name="source">The git repository link</param>
    private static bool IsGitUrl(string source)
    {
        return source.StartsWith("http://") || source.StartsWith("https://") || source.EndsWith(".git");
    }

    /// <summary>
    /// Utility to get the relative path from one directory to another.
    /// </summary>
    /// <param name="fromPath">The path of the first directory</param>
    /// <param name="toPath">The path of the second directory</param>
    private static string GetRelativePath(string fromPath, string toPath)
    {
        Uri fromUri = new Uri(fromPath);
        Uri toUri = new Uri(toPath);
        return fromUri.MakeRelativeUri(toUri).ToString().Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Tracks the progress of a package installation.
    /// </summary>
    private static void Progress()
    {
        if (_addRequest.IsCompleted)
        {
            if (_addRequest.Status == StatusCode.Success)
            {
                WwiseLogger.LogFormat(LogLevel.Log, "Successfully installed package: {0}", _addRequest.Result.packageId);
            }
            else if (_addRequest.Status >= StatusCode.Failure)
            {
                WwiseLogger.LogFormat(LogLevel.Error, "Failed to install package: {0}", _addRequest.Error.message);
            }

            EditorApplication.update -= Progress;
        }
    }
    
#if AK_WWISE_ADDRESSABLES
    /*
     *  Uninstall
     */
    
    /// <summary>
    /// Clean up the current Addressables Assets that are in use before starting the Wwise Addressables Package uninstallation process.
    /// </summary>
    private static void CleanupAddressables()
    {
        AkAddressableBankManager.Instance.DoUnloadBank();

        // Force Addressables Build Cleanup
        AddressableAssetSettings.CleanPlayerContent();

        // Clear Unity Cache
        Caching.ClearCache();

        // Refresh Unity Database
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        WwiseLogger.Log("Addressables cleaned successfully.");
    }
    
    /// <summary>
    /// Remove the Wwise Init Bank Holder from the Wwise Global GameObject as it's a Wwise Addressables Package specific component.
    /// </summary>
    public static void RemoveInitBankHolder()
    {
        string targetObjectName = "WwiseGlobal"; // Change to the name of the object
        string targetComponentName = "InitBankHolder"; // Change to the component name
        // Find the GameObject by name
        GameObject targetObject = GameObject.Find(targetObjectName);

        if (targetObject == null)
        {
            WwiseLogger.LogFormat(LogLevel.Error, "GameObject '{0}' not found in the scene.", targetObjectName);
            return;
        }

        // Find the component by name dynamically
        Component component = targetObject.GetComponent(targetComponentName);
        if (component != null)
        {
            UnityEngine.GameObject.DestroyImmediate(component);
            WwiseLogger.LogFormat(LogLevel.Log, "Removed component '{0}' from '{1}'.", targetComponentName, targetObjectName);
        }
        else
        {
            WwiseLogger.LogFormat(LogLevel.Warning, "Component '{0}' not found on '{1}'.", targetComponentName, targetObjectName);
        }
    }
    
    /// <summary>
    /// Remove the Wwise Addressables Package through the package manager API.
    /// </summary>
    public static void RemoveWwiseAddressablePackage()
    {
        Client.Remove("com.audiokinetic.wwise.addressables");
    }
    
    /// <summary>
    /// Delete the Wwise Addressables Bank Folder and it's content from the Unity Application Data Path.
    /// </summary>
    public static void DeleteWwiseAddressablesBankFolder()
    {
        string folderPath = "Assets/" + _addressableBankFolder;

        if (AssetDatabase.IsValidFolder(folderPath))
        {
            if (AssetDatabase.DeleteAsset(folderPath))
            {
                WwiseLogger.LogFormat(LogLevel.Log, "Folder deleted: {0}", folderPath);
            }
            else
            {
                WwiseLogger.LogFormat(LogLevel.Error, "Failed to delete folder: {0}", folderPath);
            }
        }
        else
        {
            WwiseLogger.LogFormat(LogLevel.Error, "Folder does not exist: {0}", folderPath);
        }
    }
    
    /// <summary>
    /// Delete The Wwise Addressables Groups
    /// </summary>
    public static void DeleteWwiseAddressableGroups()
    {
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
        if (settings == null)
        {
            return;
        }

        string addressableGroupPrefix = "Wwise";
        var groupsToRemove = settings.groups.Where(g => g != null && g.Name.StartsWith(addressableGroupPrefix)).ToList();

        if (groupsToRemove.Count == 0)
        {
            return;
        }

        foreach (var group in groupsToRemove)
        {
            settings.RemoveGroup(group);
        }

        AssetDatabase.SaveAssets();
    }
#endif
}

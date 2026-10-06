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

using System.IO;
using AK.Wwise.Unity.Logging;

public partial class WwiseProjectDatabase
{
    public static bool ProjectInfoExists = false;
    public static void InitCheckUp(string inDirectoryPath)
    {
        var jsonFilename = Path.Combine(inDirectoryPath, "ProjectInfo.json");
        ProjectInfoExists = File.Exists(jsonFilename);
    }

    public static string PlatformName = "";
    public static void LogProjectDatabaseNotFound(string errorMessage)
    {
        int index = errorMessage.IndexOf(' '); // Find the index of the first space
        string libraryName = index != -1 ? errorMessage.Substring(0, index) : errorMessage;
        string directory = "Assets/Wwise/ProjectDatabase/Runtime/Plugins";
        if (PlatformName == "Windows")
        {
            bool x86Architecture = System.IntPtr.Size == 4;
            directory += "/Windows/" + (x86Architecture ? "x86" : "x86_64");
        }
        else if (PlatformName == "Mac")
        {
            directory += "/Mac/DSP";
        }
        else if (PlatformName == "Linux")
        {
            bool x86Architecture = System.IntPtr.Size == 4;
            directory += "/Linux/" + (x86Architecture ? "x86" : "x86_64") + "/DSP";
        }
        
        WwiseLogger.LogFormat(LogLevel.Error, "{0} could not be found. Please check the parent folder {1}. If the {2} is missing, try 1. Modifying the Wwise Project or 2. Copying the {0} directly from the SDK\\platform_architecture\\Profile\\bin folder of your Wwise installation into {1}.", libraryName, directory, Path.GetExtension(libraryName));
    }

    public static void LogProjectDatabaseDLLException(System.Exception e)
    {
        if (e is System.DllNotFoundException)
        {
            LogProjectDatabaseNotFound(e.Message);
        }
        else
        {
            WwiseLogger.LogFormat(LogLevel.Error, "The project database dll encountered the following error: {0}", e.Message);
        }
    }
}

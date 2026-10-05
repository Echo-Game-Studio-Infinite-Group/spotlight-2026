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

using UnityEditor;
using UnityEngine;

namespace AK.Wwise.Unity.Logging
{
    public enum LogLevel
    {
        /// Silence all logs. Note: This will cause builds to pass, even if Wwise has errors.
        None,
        /// Only log Errors
        Error,
        /// Only log Errors and Warnings
        Warning,
        /// Default Level
        Log,
        /// Verbose Logging
        Verbose,
        /// Very Verbose Logging
        VeryVerbose
    }
    
    public class WwiseLoggerSettings: ScriptableObject
    {
        [UnityEngine.SerializeField]
        private LogLevel m_logLevel = LogLevel.Log;
        
        private static WwiseLoggerSettings _instance;
        
        private static string WwiseResources
        {
            get { return System.IO.Path.Combine(System.IO.Path.Combine("Assets", "Wwise"), "Resources"); }
        }
        
        private static string path = System.IO.Path.Combine(WwiseResources, "WwiseLoggerSettings.asset");

        public static WwiseLoggerSettings Instance
        {
            get
            {
                
                if (_instance == null)
                {
                    _instance = Resources.Load<WwiseLoggerSettings>("WwiseLoggerSettings");

                    if (_instance == null)
                    {
                        
                        _instance = CreateInstance<WwiseLoggerSettings>();
#if UNITY_EDITOR
                        if (!AssetDatabase.IsValidFolder(WwiseResources))
                        {
                            AssetDatabase.CreateFolder(System.IO.Path.Combine("Assets", "Wwise"), "Resources");
                        }

                        string assetPath = path;
                        AssetDatabase.CreateAsset(_instance, assetPath);
                        AssetDatabase.SaveAssets();
                        AssetDatabase.Refresh();
#endif
                    }
                }
                return _instance;
            }
        }

        public LogLevel LogLevel
        {
            get => m_logLevel;
            set
            {
                m_logLevel = value;
                WwiseLogger.RefreshLogLevel();
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            WwiseLogger.RefreshLogLevel();
        }
#endif
    }
}
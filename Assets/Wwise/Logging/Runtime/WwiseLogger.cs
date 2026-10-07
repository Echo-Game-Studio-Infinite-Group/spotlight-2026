#if !(UNITY_QNX) // Disable under unsupported platforms.
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

using System.Diagnostics;

namespace AK.Wwise.Unity.Logging
{
    /// <summary>
    /// The logging class for the Wwise Unity Integration plugin.
    /// Messages will be logged/swallowed according to the log level set in <see cref="WwiseLoggerSettings"/>.
    ///
    /// Log calls are stripped out entirely in release builds.
    /// To re-enable logging in a release build, add the WWISE_ENABLE_LOGS_IN_RELEASE scripting define.
    /// </summary>
    public static class WwiseLogger
    {
        private const string WwiseUnityMessagePrefix = "WwiseUnity: ";

        #region Deprecation Strings

        private const string DeprecationNotice = "This functionality is deprecated and will be removed in a future major release.";
        #endregion

        private static LogLevel s_logLevel = LogLevel.Log;

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        public static void RefreshLogLevel()
        {
            s_logLevel = WwiseLoggerSettings.Instance.LogLevel;
        }

        /// <summary>
        /// Log a WwiseUnity error message.
        /// </summary>
        /// <param name="message">Message to log</param>
#if UNITY_6000_6_OR_NEWER
        [Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
#else
        [Conditional("DEVELOPMENT_BUILD")]
#endif
        [Conditional("UNITY_EDITOR")]
        [Conditional("WWISE_ENABLE_LOGS_IN_RELEASE")]
        public static void Error(string message)
        {
            Log(LogLevel.Error, message);
        }

        /// <summary>
        /// Log a WwiseUnity warning message.
        /// </summary>
        /// <param name="message">Message to log</param>
#if UNITY_6000_6_OR_NEWER
        [Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
#else
        [Conditional("DEVELOPMENT_BUILD")]
#endif
        [Conditional("UNITY_EDITOR")]
        [Conditional("WWISE_ENABLE_LOGS_IN_RELEASE")]
        public static void Warning(string message)
        {
            Log(LogLevel.Warning, message);
        }

        /// <summary>
        /// Log a WwiseUnity message.
        /// </summary>
        /// <param name="message">Message to log</param>
#if UNITY_6000_6_OR_NEWER
        [Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
#else
        [Conditional("DEVELOPMENT_BUILD")]
#endif
        [Conditional("UNITY_EDITOR")]
        [Conditional("WWISE_ENABLE_LOGS_IN_RELEASE")]
        public static void Log(string message)
        {
            Log(LogLevel.Log, message);
        }

        /// <summary>
        /// Log a WwiseUnity message.
        /// </summary>
        /// <param name="message">Message to log</param>
#if UNITY_6000_6_OR_NEWER
        [Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
#else
        [Conditional("DEVELOPMENT_BUILD")]
#endif
        [Conditional("UNITY_EDITOR")]
        [Conditional("WWISE_ENABLE_LOGS_IN_RELEASE")]
        public static void Verbose(string message)
        {
            Log(LogLevel.Verbose, message);
        }

        /// <summary>
        /// Log a WwiseUnity very verbose message.
        /// </summary>
        /// <param name="message">Message to log</param>
#if UNITY_6000_6_OR_NEWER
        [Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
#else
        [Conditional("DEVELOPMENT_BUILD")]
#endif
        [Conditional("UNITY_EDITOR")]
        [Conditional("WWISE_ENABLE_LOGS_IN_RELEASE")]
        public static void VeryVerbose(string message)
        {
            Log(LogLevel.VeryVerbose, message);
        }

        /// <summary>
        /// Log a formatted WwiseUnity error message.
        /// </summary>
        /// <param name="format">Formatting string</param>
        /// <param name="args">Formatting arguments</param>
        [System.Obsolete(DeprecationNotice + " Use LogFormat(LogLevel.Error, format, args) instead.")]
#if UNITY_6000_6_OR_NEWER
        [Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
#else
        [Conditional("DEVELOPMENT_BUILD")]
#endif
        [Conditional("UNITY_EDITOR")]
        [Conditional("WWISE_ENABLE_LOGS_IN_RELEASE")]
        public static void ErrorFormat(string format, params object[] args)
        {
            LogFormat(LogLevel.Error, format, args);
        }

        /// <summary>
        /// Log a formatted WwiseUnity warning message.
        /// </summary>
        /// <param name="format">Formatting string</param>
        /// <param name="args">Formatting arguments</param>
        [System.Obsolete(DeprecationNotice + " Use LogFormat(LogLevel.Warning, format, args) instead.")]
#if UNITY_6000_6_OR_NEWER
        [Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
#else
        [Conditional("DEVELOPMENT_BUILD")]
#endif
        [Conditional("UNITY_EDITOR")]
        [Conditional("WWISE_ENABLE_LOGS_IN_RELEASE")]
        public static void WarningFormat(string format, params object[] args)
        {
            LogFormat(LogLevel.Warning, format, args);
        }

        /// <summary>
        /// Log a formatted WwiseUnity message.
        /// </summary>
        /// <param name="format">Formatting string</param>
        /// <param name="args">Formatting arguments</param>
        [System.Obsolete(DeprecationNotice + " Use LogFormat(LogLevel.Log, format, args) instead.")]
#if UNITY_6000_6_OR_NEWER
        [Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
#else
        [Conditional("DEVELOPMENT_BUILD")]
#endif
        [Conditional("UNITY_EDITOR")]
        [Conditional("WWISE_ENABLE_LOGS_IN_RELEASE")]
        public static void LogFormat(string format, params object[] args)
        {
            LogFormat(LogLevel.Log, format, args);
        }

        /// <summary>
        /// Log a formatted WwiseUnity verbose message.
        /// </summary>
        /// <param name="format">Formatting string</param>
        /// <param name="args">Formatting arguments</param>
        [System.Obsolete(DeprecationNotice + " Use LogFormat(LogLevel.Log, LogLevel.Verbose, format, args) instead.")]
#if UNITY_6000_6_OR_NEWER
        [Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
#else
        [Conditional("DEVELOPMENT_BUILD")]
#endif
        [Conditional("UNITY_EDITOR")]
        [Conditional("WWISE_ENABLE_LOGS_IN_RELEASE")]
        public static void VerboseFormat(string format, params object[] args)
        {
            LogFormat(LogLevel.Verbose, format, args);
        }

        /// <summary>
        /// Log a formatted WwiseUnity very verbose message.
        /// </summary>
        /// <param name="format">Formatting string</param>
        /// <param name="args">Formatting arguments</param>
        [System.Obsolete(DeprecationNotice + " Use LogFormat(LogLevel.Log, LogLevel.VeryVerbose, format, args) instead.")]
#if UNITY_6000_6_OR_NEWER
        [Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
#else
        [Conditional("DEVELOPMENT_BUILD")]
#endif
        [Conditional("UNITY_EDITOR")]
        [Conditional("WWISE_ENABLE_LOGS_IN_RELEASE")]
        public static void VeryVerboseFormat(string format, params object[] args)
        {
            LogFormat(LogLevel.VeryVerbose, format, args);
        }

        /// <summary>
        /// Log a WwiseUnity message.
        /// </summary>
        /// <param name="logLevel">Log verbosity</param>
        /// <param name="message">Message to log</param>
#if UNITY_6000_6_OR_NEWER
        [Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
#else
        [Conditional("DEVELOPMENT_BUILD")]
#endif
        [Conditional("UNITY_EDITOR")]
        [Conditional("WWISE_ENABLE_LOGS_IN_RELEASE")]
        public static void Log(LogLevel logLevel, string message)
        {
            if (s_logLevel >= logLevel)
            {
                switch (logLevel)
                {
                    case LogLevel.None:
                        break;
                    case LogLevel.Error:
                        UnityEngine.Debug.LogError(WwiseUnityMessagePrefix + "(ERROR) "+ message);
                        break;
                    case LogLevel.Warning:
                        UnityEngine.Debug.LogWarning(WwiseUnityMessagePrefix + "(WARNING) "+ message);
                        break;
                    case LogLevel.Log:
                        UnityEngine.Debug.Log(WwiseUnityMessagePrefix + "(LOG) "+ message);
                        break;
                    case LogLevel.Verbose:
                        UnityEngine.Debug.Log(WwiseUnityMessagePrefix + "(VERBOSE) "+ message);
                        break;
                    case LogLevel.VeryVerbose:
                        UnityEngine.Debug.Log(WwiseUnityMessagePrefix + "(VERYVERBOSE) "+ message);
                        break;
                }
            }
        }

        /// <summary>
        /// Log a formatted WwiseUnity message, using the Unity logger.
        /// Messages are prefixed with "WwiseUnity: "
        /// Uses the same formatting conventions as Unity.
        /// This is a fallback for when the number args exceeds the generic versions' number of args.
        /// </summary>
        /// <param name="logLevel">Log verbosity</param>
        /// <param name="format">Formatting string</param>
        /// <param name="args">Formatting arguments</param>
#if UNITY_6000_6_OR_NEWER
        [Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
#else
        [Conditional("DEVELOPMENT_BUILD")]
#endif
        [Conditional("UNITY_EDITOR")]
        [Conditional("WWISE_ENABLE_LOGS_IN_RELEASE")]
        public static void LogFormat(LogLevel logLevel, string format, params object[] args)
        {
            if (s_logLevel >= logLevel)
            {
                LogToUnity(logLevel, format, args);
            }
        }

        /// <summary>
        /// Log a formatted WwiseUnity message, using the Unity logger.
        /// Messages are prefixed with "WwiseUnity: "
        /// Uses the same formatting conventions as Unity.
        /// </summary>
        /// <param name="logLevel">Log verbosity</param>
        /// <param name="format">Formatting string</param>
        /// <param name="arg1">1st arg</param>
#if UNITY_6000_6_OR_NEWER
        [Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
#else
        [Conditional("DEVELOPMENT_BUILD")]
#endif
        [Conditional("UNITY_EDITOR")]
        [Conditional("WWISE_ENABLE_LOGS_IN_RELEASE")]
        public static void LogFormat<T1>(LogLevel logLevel, string format, T1 arg1)
        {
            if (s_logLevel >= logLevel)
            {
                LogToUnity(logLevel, format, arg1);
            }
        }

        /// <summary>
        /// Log a formatted WwiseUnity message, using the Unity logger.
        /// Messages are prefixed with "WwiseUnity: "
        /// Uses the same formatting conventions as Unity.
        /// </summary>
        /// <param name="logLevel">Log verbosity</param>
        /// <param name="format">Formatting string</param>
        /// <param name="arg1">1st arg</param>
        /// <param name="arg2">2nd arg</param>
#if UNITY_6000_6_OR_NEWER
        [Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
#else
        [Conditional("DEVELOPMENT_BUILD")]
#endif
        [Conditional("UNITY_EDITOR")]
        [Conditional("WWISE_ENABLE_LOGS_IN_RELEASE")]
        public static void LogFormat<T1, T2>(LogLevel logLevel, string format, T1 arg1, T2 arg2)
        {
            if (s_logLevel >= logLevel)
            {
                LogToUnity(logLevel, format, arg1, arg2);
            }
        }

        /// <summary>
        /// Log a formatted WwiseUnity message, using the Unity logger.
        /// Messages are prefixed with "WwiseUnity: "
        /// Uses the same formatting conventions as Unity.
        /// </summary>
        /// <param name="logLevel">Log verbosity</param>
        /// <param name="format">Formatting string</param>
        /// <param name="arg1">1st arg</param>
        /// <param name="arg2">2nd arg</param>
        /// <param name="arg3">3rd arg</param>
#if UNITY_6000_6_OR_NEWER
        [Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
#else
        [Conditional("DEVELOPMENT_BUILD")]
#endif
        [Conditional("UNITY_EDITOR")]
        [Conditional("WWISE_ENABLE_LOGS_IN_RELEASE")]
        public static void LogFormat<T1, T2, T3>(LogLevel logLevel, string format, T1 arg1, T2 arg2, T3 arg3)
        {
            if (s_logLevel >= logLevel)
            {
                LogToUnity(logLevel, format, arg1, arg2, arg3);
            }
        }

        /// <summary>
        /// Log a formatted WwiseUnity message, using the Unity logger.
        /// Messages are prefixed with "WwiseUnity: "
        /// Uses the same formatting conventions as Unity.
        /// </summary>
        /// <param name="logLevel">Log verbosity</param>
        /// <param name="format">Formatting string</param>
        /// <param name="arg1">1st arg</param>
        /// <param name="arg2">2nd arg</param>
        /// <param name="arg3">3rd arg</param>
        /// <param name="arg4">4th arg</param>
#if UNITY_6000_6_OR_NEWER
        [Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
#else
        [Conditional("DEVELOPMENT_BUILD")]
#endif
        [Conditional("UNITY_EDITOR")]
        [Conditional("WWISE_ENABLE_LOGS_IN_RELEASE")]
        public static void LogFormat<T1, T2, T3, T4>(LogLevel logLevel, string format, T1 arg1, T2 arg2, T3 arg3, T4 arg4)
        {
            if (s_logLevel >= logLevel)
            {
                LogToUnity(logLevel, format, arg1, arg2, arg3, arg4);
            }
        }

        /// <summary>
        /// Log a formatted WwiseUnity message, using the Unity logger.
        /// Messages are prefixed with "WwiseUnity: "
        /// Uses the same formatting conventions as Unity.
        /// </summary>
        /// <param name="logLevel">Log verbosity</param>
        /// <param name="format">Formatting string</param>
        /// <param name="arg1">1st arg</param>
        /// <param name="arg2">2nd arg</param>
        /// <param name="arg3">3rd arg</param>
        /// <param name="arg4">4th arg</param>
        /// <param name="arg5">5th arg</param>
#if UNITY_6000_6_OR_NEWER
        [Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
#else
        [Conditional("DEVELOPMENT_BUILD")]
#endif
        [Conditional("UNITY_EDITOR")]
        [Conditional("WWISE_ENABLE_LOGS_IN_RELEASE")]
        public static void LogFormat<T1, T2, T3, T4, T5>(LogLevel logLevel, string format, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5)
        {
            if (s_logLevel >= logLevel)
            {
                LogToUnity(logLevel, format, arg1, arg2, arg3, arg4, arg5);
            }
        }

        /// <summary>
        /// Log a formatted WwiseUnity message, using the Unity logger.
        /// Messages are prefixed with "WwiseUnity: "
        /// Uses the same formatting conventions as Unity.
        /// </summary>
        /// <param name="logLevel">Log verbosity</param>
        /// <param name="format">Formatting string</param>
        /// <param name="arg1">1st arg</param>
        /// <param name="arg2">2nd arg</param>
        /// <param name="arg3">3rd arg</param>
        /// <param name="arg4">4th arg</param>
        /// <param name="arg5">5th arg</param>
        /// <param name="arg6">6th arg</param>
#if UNITY_6000_6_OR_NEWER
        [Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
#else
        [Conditional("DEVELOPMENT_BUILD")]
#endif
        [Conditional("UNITY_EDITOR")]
        [Conditional("WWISE_ENABLE_LOGS_IN_RELEASE")]
        public static void LogFormat<T1, T2, T3, T4, T5, T6>(LogLevel logLevel, string format, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6)
        {
            if (s_logLevel >= logLevel)
            {
                LogToUnity(logLevel, format, arg1, arg2, arg3, arg4, arg5, arg6);
            }
        }

        /// <summary>
        /// Log a formatted WwiseUnity message, using the Unity logger.
        /// Messages are prefixed with "WwiseUnity: "
        /// Uses the same formatting conventions as Unity.
        /// </summary>
        /// <param name="logLevel">Log verbosity</param>
        /// <param name="format">Formatting string</param>
        /// <param name="arg1">1st arg</param>
        /// <param name="arg2">2nd arg</param>
        /// <param name="arg3">3rd arg</param>
        /// <param name="arg4">4th arg</param>
        /// <param name="arg5">5th arg</param>
        /// <param name="arg6">6th arg</param>
        /// <param name="arg7">7th arg</param>
#if UNITY_6000_6_OR_NEWER
        [Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
#else
        [Conditional("DEVELOPMENT_BUILD")]
#endif
        [Conditional("UNITY_EDITOR")]
        [Conditional("WWISE_ENABLE_LOGS_IN_RELEASE")]
        public static void LogFormat<T1, T2, T3, T4, T5, T6, T7>(LogLevel logLevel, string format, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7)
        {
            if (s_logLevel >= logLevel)
            {
                LogToUnity(logLevel, format, arg1, arg2, arg3, arg4, arg5, arg6, arg7);
            }
        }

        /// <summary>
        /// Log a formatted WwiseUnity message, using the Unity logger.
        /// Messages are prefixed with "WwiseUnity: "
        /// Uses the same formatting conventions as Unity.
        /// </summary>
        /// <param name="logLevel">Log verbosity</param>
        /// <param name="format">Formatting string</param>
        /// <param name="arg1">1st arg</param>
        /// <param name="arg2">2nd arg</param>
        /// <param name="arg3">3rd arg</param>
        /// <param name="arg4">4th arg</param>
        /// <param name="arg5">5th arg</param>
        /// <param name="arg6">6th arg</param>
        /// <param name="arg7">7th arg</param>
        /// <param name="arg8">8th arg</param>
#if UNITY_6000_6_OR_NEWER
        [Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
#else
        [Conditional("DEVELOPMENT_BUILD")]
#endif
        [Conditional("UNITY_EDITOR")]
        [Conditional("WWISE_ENABLE_LOGS_IN_RELEASE")]
        public static void LogFormat<T1, T2, T3, T4, T5, T6, T7, T8>(LogLevel logLevel, string format, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8)
        {
            if (s_logLevel >= logLevel)
            {
                LogToUnity(logLevel, format, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8);
            }
        }

        /// <summary>
        /// Log a formatted WwiseUnity message, using the Unity logger.
        /// Messages are prefixed with "WwiseUnity: "
        /// Uses the same formatting conventions as Unity.
        /// </summary>
        /// <param name="logLevel">Log verbosity</param>
        /// <param name="format">Formatting string</param>
        /// <param name="arg1">1st arg</param>
        /// <param name="arg2">2nd arg</param>
        /// <param name="arg3">3rd arg</param>
        /// <param name="arg4">4th arg</param>
        /// <param name="arg5">5th arg</param>
        /// <param name="arg6">6th arg</param>
        /// <param name="arg7">7th arg</param>
        /// <param name="arg8">8th arg</param>
        /// <param name="arg9">9th arg</param>
#if UNITY_6000_6_OR_NEWER
        [Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
#else
        [Conditional("DEVELOPMENT_BUILD")]
#endif
        [Conditional("UNITY_EDITOR")]
        [Conditional("WWISE_ENABLE_LOGS_IN_RELEASE")]
        public static void LogFormat<T1, T2, T3, T4, T5, T6, T7, T8, T9>(LogLevel logLevel, string format, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, T9 arg9)
        {
            if (s_logLevel >= logLevel)
            {
                LogToUnity(logLevel, format, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9);
            }
        }

        /// <summary>
        /// Log a formatted WwiseUnity message, using the Unity logger.
        /// Messages are prefixed with "WwiseUnity: "
        /// Uses the same formatting conventions as Unity.
        /// </summary>
        /// <param name="logLevel">Log verbosity</param>
        /// <param name="format">Formatting string</param>
        /// <param name="arg1">1st arg</param>
        /// <param name="arg2">2nd arg</param>
        /// <param name="arg3">3rd arg</param>
        /// <param name="arg4">4th arg</param>
        /// <param name="arg5">5th arg</param>
        /// <param name="arg6">6th arg</param>
        /// <param name="arg7">7th arg</param>
        /// <param name="arg8">8th arg</param>
        /// <param name="arg9">9th arg</param>
        /// <param name="arg10">10th arg</param>
#if UNITY_6000_6_OR_NEWER
        [Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
#else
        [Conditional("DEVELOPMENT_BUILD")]
#endif
        [Conditional("UNITY_EDITOR")]
        [Conditional("WWISE_ENABLE_LOGS_IN_RELEASE")]
        public static void LogFormat<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(LogLevel logLevel, string format, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, T9 arg9, T10 arg10)
        {
            if (s_logLevel >= logLevel)
            {
                LogToUnity(logLevel, format, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10);
            }
        }

        /// <summary>
        /// Log a formatted WwiseUnity message, using the Unity logger.
        /// Messages are prefixed with "WwiseUnity: "
        /// Uses the same formatting conventions as Unity.
        /// </summary>
        /// <param name="logLevel">Log verbosity</param>
        /// <param name="format">Formatting string</param>
        /// <param name="arg1">1st arg</param>
        /// <param name="arg2">2nd arg</param>
        /// <param name="arg3">3rd arg</param>
        /// <param name="arg4">4th arg</param>
        /// <param name="arg5">5th arg</param>
        /// <param name="arg6">6th arg</param>
        /// <param name="arg7">7th arg</param>
        /// <param name="arg8">8th arg</param>
        /// <param name="arg9">9th arg</param>
        /// <param name="arg10">10th arg</param>
        /// <param name="arg11">11th arg</param>
#if UNITY_6000_6_OR_NEWER
        [Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
#else
        [Conditional("DEVELOPMENT_BUILD")]
#endif
        [Conditional("UNITY_EDITOR")]
        [Conditional("WWISE_ENABLE_LOGS_IN_RELEASE")]
        public static void LogFormat<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(LogLevel logLevel, string format, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, T9 arg9, T10 arg10, T11 arg11)
        {
            if (s_logLevel >= logLevel)
            {
                LogToUnity(logLevel, format, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11);
            }
        }

        /// <summary>
        /// Log a formatted WwiseUnity message, using the Unity logger.
        /// Messages are prefixed with "WwiseUnity: "
        /// Uses the same formatting conventions as Unity.
        /// </summary>
        /// <param name="logLevel">Log verbosity</param>
        /// <param name="format">Formatting string</param>
        /// <param name="arg1">1st arg</param>
        /// <param name="arg2">2nd arg</param>
        /// <param name="arg3">3rd arg</param>
        /// <param name="arg4">4th arg</param>
        /// <param name="arg5">5th arg</param>
        /// <param name="arg6">6th arg</param>
        /// <param name="arg7">7th arg</param>
        /// <param name="arg8">8th arg</param>
        /// <param name="arg9">9th arg</param>
        /// <param name="arg10">10th arg</param>
        /// <param name="arg11">11th arg</param>
        /// <param name="arg12">12th arg</param>
#if UNITY_6000_6_OR_NEWER
        [Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
#else
        [Conditional("DEVELOPMENT_BUILD")]
#endif
        [Conditional("UNITY_EDITOR")]
        [Conditional("WWISE_ENABLE_LOGS_IN_RELEASE")]
        public static void LogFormat<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(LogLevel logLevel, string format, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, T9 arg9, T10 arg10, T11 arg11, T12 arg12)
        {
            if (s_logLevel >= logLevel)
            {
                LogToUnity(logLevel, format, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11, arg12);
            }
        }

        private static void LogToUnity(LogLevel logLevel, string format, params object[] args)
        {
            switch (logLevel)
            {
                case LogLevel.None:
                    break;
                case LogLevel.Error:
                    UnityEngine.Debug.LogErrorFormat(WwiseUnityMessagePrefix + "(ERROR) " + format, args);
                    break;
                case LogLevel.Warning:
                    UnityEngine.Debug.LogWarningFormat(WwiseUnityMessagePrefix + "(WARNING) " + format, args);
                    break;
                case LogLevel.Log:
                    UnityEngine.Debug.LogFormat(WwiseUnityMessagePrefix + "(LOG) " + format, args);
                    break;
                case LogLevel.Verbose:
                    UnityEngine.Debug.LogFormat( WwiseUnityMessagePrefix + "(VERBOSE) " + format, args);
                    break;
                case LogLevel.VeryVerbose:
                    UnityEngine.Debug.LogFormat(WwiseUnityMessagePrefix + "(VERYVERBOSE) " + format, args);
                    break;
            }
        }
    }
}

#endif // #if !(UNITY_QNX) // Disable under unsupported platforms.

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


using System.Collections;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Wwise.API.Runtime.WwiseTypes.WwiseObjectsManagers;
using AK.Wwise.Unity.Logging;
#if AK_WWISE_ADDRESSABLES && UNITY_ADDRESSABLES
using AK.Wwise.Unity.WwiseAddressables;
#endif
/// @brief Represents Wwise events as Unity assets.
[System.Serializable]
public class WwiseEventReference : WwiseObjectReference
#if UNITY_EDITOR
	, AK.Wwise.IMigratable
#endif
{
#if AK_WWISE_ADDRESSABLES && UNITY_ADDRESSABLES
	[UnityEngine.SerializeField, AkShowOnly]
	public WwiseAddressableSoundBank AutoBank;

#endif
	public override WwiseObjectType WwiseObjectType { get { return WwiseObjectType.Event; } }
	public bool IsInUserDefinedSoundBank = false;
	private uint m_BankID = AkUnitySoundEngine.AK_INVALID_UNIQUE_ID;
	private bool pendingEnable = false;
	[System.NonSerialized]
	public bool IsAutoBankLoaded = false;

	private AkBankTypeEnum BankType
	{
		get
		{
			if (WwiseObjectType == WwiseObjectType.AuxBus)
			{
				return AkBankTypeEnum.AkBankType_Bus;
			}
			else if (WwiseObjectType == WwiseObjectType.Event)
			{
				return AkBankTypeEnum.AkBankType_Event;
			}
			else
			{
				return AkBankTypeEnum.AkBankType_User;
			}
		}
	}

	protected void PostLoadAutoBank(uint bankId)
	{
		if (bankId == AkUnitySoundEngine.AK_INVALID_UNIQUE_ID)
		{
			return;
		}

		var result = AkUnitySoundEngine.PrepareEvent(AkPreparationType.Preparation_Load, new string[] { DisplayName }, 1);
		if (result != AKRESULT.AK_Success)
		{
			WwiseLogger.LogFormat(LogLevel.Error, "PrepareEvent for {0} failed with result: {1}. If the event is in a User Defined Soundbank, make sure to check the \"Is In User-Defined SoundBank\" box in the editor.", DisplayName, result);
		}
	}

#if AK_WWISE_ADDRESSABLES && UNITY_ADDRESSABLES
#if UNITY_WEBGL
	public IEnumerator CompleteLoadBank()
	{
		while (!IsAutoBankLoaded && AutoBank.LoadState != BankLoadState.Unloaded)
		{
			yield return null;
		}
	}
#else

	private async Task CompleteLoadBankInternal()
	{
		while (AutoBank != null && !IsAutoBankLoaded && AutoBank.LoadState != BankLoadState.Unloaded)
		{
			await Task.Yield();
		}
	}

	//Wait for the Bank to be loaded or a given timeout (in ms)
	public async Task CompleteLoadBank(int timeoutms = 5000)
	{
		var timeoutTask = Task.Delay(timeoutms);
		Task completedTask = await Task.WhenAny(CompleteLoadBankInternal(), timeoutTask);
		await completedTask;
	}
#endif
	
#if UNITY_EDITOR
	
	public override void CompleteData()
	{
#if WWISE_ADDRESSABLES_24_1_OR_LATER
		SetAddressableBank(AkAssetUtilities.GetAddressableBankAsset(DisplayName, true));
#else
		SetAddressableBank(AkAssetUtilities.GetAddressableBankAsset(DisplayName));
#endif
	}

	public override bool IsComplete()
	{
		return (AutoBank != null && IsInUserDefinedSoundBank == false) || (AutoBank == null && IsInUserDefinedSoundBank == true);
	}


	public void SetAddressableBank(WwiseAddressableSoundBank asset)
	{
		AutoBank = asset;
		IsInUserDefinedSoundBank = asset == null;
		EditorUtility.SetDirty(this);
		AssetDatabase.SaveAssets();
		AssetDatabase.Refresh();
	}
	public static void FindEventReferenceAndSetAddressableBank(WwiseAddressableSoundBank addressableAsset, string name)
	{
		var guids = UnityEditor.AssetDatabase.FindAssets("t:" + typeof(WwiseEventReference).Name);
		WwiseEventReference asset;
		foreach (var assetGuid in guids)
		{
			var assetPath = UnityEditor.AssetDatabase.GUIDToAssetPath(assetGuid);
			asset = UnityEditor.AssetDatabase.LoadAssetAtPath<WwiseEventReference>(assetPath);
			if (asset && asset.ObjectName == name)
			{
				asset.SetAddressableBank(addressableAsset);
			}
		}
	}
#endif
#endif
#if AK_WWISE_ADDRESSABLES && UNITY_ADDRESSABLES && WWISE_ADDRESSABLES_24_1_OR_LATER
	public async Task LoadAutoBankAsync()
	{
		if (AutoBank != null)
		{
			AutoBank.IsAutoBank = !IsInUserDefinedSoundBank;
			AutoBank.BankType = (uint)AkBankTypeEnum.AkBankType_Event;
			await AkAddressableBankManager.Instance.LoadBank(AutoBank, false, false, loadAsync:true);
			WwiseEventReferencesManager.Instance.AddReference(this);
		}
		
		else
		{
			WwiseLogger.LogFormat(LogLevel.Warning, "Wwise Addressable asset for AutoBank: {0} couldn't be found. If the event is in a User-Defined Soundbank, make sure to check the \"Is In User-Defined SoundBank\" box in the editor.", DisplayName);
		}
	}
#else
	public void LoadAutoBankAsync()
	{
		m_BankID = AkBankManager.LoadBank(DisplayName, false, false, BankType);
		WwiseEventReferencesManager.Instance.AddReference(this);
		PostLoadAutoBank(m_BankID);
		IsAutoBankLoaded = true;
	}
#endif
	
#if AK_WWISE_ADDRESSABLES && UNITY_ADDRESSABLES
#if WWISE_ADDRESSABLES_24_1_OR_LATER
	public void OnAutoBankLoaded()
	{
		if (AutoBank == null)
		{
			return;
		}
		m_BankID = AutoBank.SoundbankId;
		PostLoadAutoBank(m_BankID);
		AkAddressableBankManager.Instance.OnAutoBankLoaded(AutoBank);
		IsAutoBankLoaded = true;
	}
#endif
#endif

	public void LoadAutoBank()
	{
#if AK_WWISE_ADDRESSABLES && UNITY_ADDRESSABLES && WWISE_ADDRESSABLES_24_1_OR_LATER
		if (AutoBank != null)
		{
			AutoBank.OnBankLoaded += OnAutoBankLoaded;
		}
#endif
		if (IsInUserDefinedSoundBank || !AkWwiseInitializationSettings.Instance.IsAutoBankEnabled)
		{
			return;
		}
#if AK_WWISE_ADDRESSABLES && UNITY_ADDRESSABLES
#if WWISE_ADDRESSABLES_24_1_OR_LATER
		if (AutoBank != null && AutoBank.LoadState == BankLoadState.Loading)
			return;
		_ = LoadAutoBankAsync();
#else
		WwiseLogger.LogFormat(LogLevel.Error, "Auto Bank is not supported with Addressables prior to the Wwise Addressables Package 24.1. Please update your wwise Addressables package or add the event: {0} to an user-defined soundbank in Wwise Authoring.", DisplayName);
#endif //WWISE_ADDRESSABLES_24_1_OR_LATER
#else
		LoadAutoBankAsync();
#endif
	}

	public void ReloadAutoBank()
	{
		UnloadAutoBank();
#if UNITY_EDITOR
		//Can be triggered from going in and out of PIE.
		if (!Application.isPlaying)
			//Asset fails to load when is not playing (in editor).
			return;
#endif
		LoadAutoBank();
	}

	private void EnableSequence()
	{
#if UNITY_EDITOR
		WwiseProjectDatabase.SoundBankDirectoryUpdated += UpdateIsUserDefinedSoundBank;
#endif
		if (AkUnitySoundEngine.IsInitialized())
		{
			LoadAutoBank();
		}
		else
		{
			AkUnitySoundEngineInitialization.Instance.initializationDelegate += LoadAutoBank;
		}
		AkUnitySoundEngineInitialization.Instance.reInitializationDelegate += ReloadAutoBank;
		AkUnitySoundEngineInitialization.Instance.terminationDelegate += UnloadAutoBankOnSoundEngineTermination;
	}
	
#if UNITY_EDITOR
	private void OnPlayModeStateChanged(PlayModeStateChange state)
	{
		if (pendingEnable && (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.EnteredPlayMode))
		{
			EnableSequence();
			EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
			pendingEnable = false;
		}
	}
#endif
	
	private void OnEnable()
	{
#if UNITY_EDITOR
		//Do not load the bank if the editor is not currently playing. In order to get the information from an AkEvent, the associated Scriptable Object has to be loaded which would otherwise trigger the load of the bank.
		if (Application.isEditor && !EditorApplication.isPlayingOrWillChangePlaymode)
		{
			pendingEnable = true;
			EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
			return;
		}
#endif
		EnableSequence();
	}

#if UNITY_EDITOR
	public void UpdateIsUserDefinedSoundBank()
	{
		if (WwiseProjectDatabase.ProjectInfoExists)
		{
			WwiseSoundBankRef soundBankRef = new WwiseSoundBankRef(DisplayName, "Event");
#if AK_WWISE_ADDRESSABLES && UNITY_ADDRESSABLES
			if (!soundBankRef.IsValid && AutoBank != null)
			{
				foreach (var data in AutoBank.Data)
				{
					WwiseProjectDatabase.SetCurrentLanguage(data.Key);
					soundBankRef = new WwiseSoundBankRef(DisplayName, "Event");
					if (soundBankRef.IsValid)
						break;
				}
			}
#endif
			
			IsInUserDefinedSoundBank = !soundBankRef.IsValid;
			UnityEditor.EditorUtility.SetDirty(this);
			if (IsAutoBankLoaded)
			{
				UnloadAutoBank();
			}
			LoadAutoBank();
		}
	}
#endif

	public void UnloadAutoBankOnSoundEngineTermination()
	{
		UnloadAutoBank();
	}

	public IEnumerator UnloadAutoBank()
	{
#if AK_WWISE_ADDRESSABLES && UNITY_ADDRESSABLES
#if WWISE_ADDRESSABLES_24_1_OR_LATER
		if (AutoBank == null)
		{
			yield return null;
		}
		if (AutoBank.LoadState == BankLoadState.Loading || AutoBank.LoadState == BankLoadState.WaitingForPrepareEvent)
		{
			yield return CompleteLoadBank();
		}

		AutoBank.OnBankLoaded -= OnAutoBankLoaded;
#endif
#endif
		if (m_BankID != AkUnitySoundEngine.AK_INVALID_UNIQUE_ID && IsAutoBankLoaded)
		{
#if AK_WWISE_ADDRESSABLES && UNITY_ADDRESSABLES
			AkAddressableBankManager.Instance.UnloadBank(AutoBank, true);
#else
			AkUnitySoundEngine.PrepareEvent(AkPreparationType.Preparation_Unload, new string[] { DisplayName }, 1);
			AkBankManager.UnloadBank(DisplayName);
#endif
			m_BankID = AkUnitySoundEngine.AK_INVALID_UNIQUE_ID;
			WwiseEventReferencesManager.Instance.RemoveReference(this);
		}
		IsAutoBankLoaded = false;
		yield return null;
	}
	public void OnDisable()
	{
		UnloadAutoBank();
		AkUnitySoundEngineInitialization.Instance.initializationDelegate -= LoadAutoBank;
		AkUnitySoundEngineInitialization.Instance.reInitializationDelegate -= ReloadAutoBank;
		AkUnitySoundEngineInitialization.Instance.terminationDelegate -= UnloadAutoBankOnSoundEngineTermination;
#if UNITY_EDITOR
		WwiseProjectDatabase.SoundBankDirectoryUpdated -= UpdateIsUserDefinedSoundBank;
#endif
	}
	
#region Migration

#if UNITY_EDITOR
	bool AK.Wwise.IMigratable.Migrate(UnityEditor.SerializedObject serializedObject)
	{
		// If the object was existing before the introduction of Auto-Defined SoundBanks,
		// it has to be in a user-defined SoundBank.
		var userDefinedProperty = serializedObject.FindProperty("IsInUserDefinedSoundBank");
		if (userDefinedProperty != null)
		{
			userDefinedProperty.boolValue = true;
			return true;
		}
		return false;
	}
#endif
	#endregion
}
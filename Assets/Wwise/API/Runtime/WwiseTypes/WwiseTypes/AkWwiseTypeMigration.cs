using AK.Wwise.Unity.Logging;

#if !(UNITY_QNX) // Disable under unsupported platforms.
#if UNITY_EDITOR

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


namespace AK.Wwise
{
	public interface IMigratable
	{
		// return true if changes have been made to the serializedObject
		bool Migrate(UnityEditor.SerializedObject serializedObject);
	}

	public static class TypeMigration
	{
		private static byte[] GetByteArray(UnityEditor.SerializedProperty property)
		{
			if (property == null || !property.isArray)
				return null;

			if (property.arraySize != 16)
				return null;

			var byteArray = new byte[16];
			for (var i = 0; i < 16; ++i)
				byteArray[i] = (byte)property.GetArrayElementAtIndex(i).intValue;

			return byteArray;
		}

		private static int GetId(UnityEditor.SerializedProperty property)
		{
			if (property == null)
				return (int)AkUnitySoundEngine.AK_INVALID_UNIQUE_ID;

			switch (property.propertyType)
			{
				case UnityEditor.SerializedPropertyType.Integer:
					return property.intValue;

				case UnityEditor.SerializedPropertyType.String:
					return (int)AkUtilities.ShortIDGenerator.Compute(property.stringValue);

				default:
					return (int)AkUnitySoundEngine.AK_INVALID_UNIQUE_ID;
			}
		}

		private static WwiseObjectType GetWwiseObjectType(UnityEditor.SerializedProperty property)
		{
			var name = property.type;
			if (name == typeof(AuxBus).Name)
				return WwiseObjectType.AuxBus;
			if (name == typeof(Event).Name)
				return WwiseObjectType.Event;
			if (name == typeof(Bank).Name)
				return WwiseObjectType.Soundbank;
			if (name == typeof(State).Name)
				return WwiseObjectType.State;
			if (name == typeof(Switch).Name)
				return WwiseObjectType.Switch;
			if (name == typeof(RTPC).Name)
				return WwiseObjectType.GameParameter;
			if (name == typeof(Trigger).Name)
				return WwiseObjectType.Trigger;
			if (name == typeof(AcousticTexture).Name)
				return WwiseObjectType.AcousticTexture;

			return WwiseObjectType.None;
		}

		private static bool SetWwiseObjectReferenceProperty(UnityEditor.SerializedProperty wwiseObjRefProperty, WwiseObjectReference objRef)
		{
			var previousObjectReference = wwiseObjRefProperty.objectReferenceValue as WwiseObjectReference;
			if (previousObjectReference == objRef)
			{
				if (objRef)
				{
					WwiseLogger.LogFormat(LogLevel.Log, "WwiseObjectReference already set to <{0}> on <{1}> for type <{2}>.", objRef.DisplayName, wwiseObjRefProperty.serializedObject.targetObject, objRef.WwiseObjectType);
				}
				else
				{
					WwiseLogger.LogFormat(LogLevel.Log, "WwiseObjectReference already set to <null> on <{0}>.", wwiseObjRefProperty.serializedObject.targetObject);
				}
				return false;
			}

			if (previousObjectReference)
			{
				if (objRef)
				{
					WwiseLogger.LogFormat(LogLevel.Warning, "Overwriting WwiseObjectReference on <{0}> for type <{1}> from <{2}> to <{3}>.", wwiseObjRefProperty.serializedObject.targetObject, objRef.WwiseObjectType, previousObjectReference.DisplayName, objRef.DisplayName);
				}
				else
				{
					WwiseLogger.LogFormat(LogLevel.Warning, "Overwriting WwiseObjectReference on <{0}> from <{1}> to <null>.", wwiseObjRefProperty.serializedObject.targetObject, previousObjectReference.DisplayName);
				}
			}
			else if (objRef)
			{
				WwiseLogger.LogFormat(LogLevel.Log, "Setting WwiseObjectReference on <{0}> for type <{1}> to <{2}>.", wwiseObjRefProperty.serializedObject.targetObject, objRef.WwiseObjectType, objRef.DisplayName);
			}
			else
			{
				WwiseLogger.LogFormat(LogLevel.Log, "Setting WwiseObjectReference on <{0}> to <null>.", wwiseObjRefProperty.serializedObject.targetObject);
			}

			wwiseObjRefProperty.objectReferenceValue = objRef;
			return true;
		}

		public static bool SearchAndProcessWwiseTypes(UnityEditor.SerializedProperty property)
		{
			var anyChange = false;
			var recurse = false;
			for (var loop = property != null; loop; loop = property.Next(recurse))
			{
				recurse = false;

				if (property.isArray)
				{
					if (property.arraySize == 0)
						continue;

					recurse = property.GetArrayElementAtIndex(0).propertyType == UnityEditor.SerializedPropertyType.Generic;
				}
				else if (property.propertyType == UnityEditor.SerializedPropertyType.Generic)
				{
					recurse = true;

					var wwiseObjectType = GetWwiseObjectType(property);
					if (wwiseObjectType == WwiseObjectType.None)
						continue;

					// At this point, we know that the property's type's name is the same as one of our WwiseTypes.
					var valueGuidProperty = property.FindPropertyRelative("valueGuidInternal");
					var wwiseObjectReferenceProperty = property.FindPropertyRelative("WwiseObjectReference");
					if (valueGuidProperty == null || wwiseObjectReferenceProperty == null)
						continue;

					// At this point, we can be **pretty** sure that we are dealing with a WwiseType.
					// The uncertainty lies with the fact that the property.type is the non-qualified name of the property's type.
					recurse = false;

					var idProperty = property.FindPropertyRelative("idInternal");
					var hasChanged = false;
					if (wwiseObjectType == WwiseObjectType.State || wwiseObjectType == WwiseObjectType.Switch)
					{
						var groupGuidProperty = property.FindPropertyRelative("groupGuidInternal");
						if (groupGuidProperty == null)
							continue;

						var groupIdProperty = property.FindPropertyRelative("groupIdInternal");
						hasChanged = ProcessDoubleGuidType(wwiseObjectReferenceProperty, wwiseObjectType, valueGuidProperty, idProperty, groupGuidProperty, groupIdProperty);
					}
					else
					{
						hasChanged = ProcessSingleGuidType(wwiseObjectReferenceProperty, wwiseObjectType, valueGuidProperty, idProperty);
					}

					anyChange = anyChange || hasChanged;
				}
			}

			return anyChange;
		}

		public static bool ProcessSingleGuidType(UnityEditor.SerializedProperty wwiseObjectReferenceProperty, WwiseObjectType wwiseObjectType, 
			UnityEditor.SerializedProperty valueGuidProperty, UnityEditor.SerializedProperty idProperty)
		{
			if (wwiseObjectReferenceProperty == null)
			{
				WwiseLogger.Error("This migration step is no longer necessary.");
				return false;
			}

			var valueGuid = GetByteArray(valueGuidProperty);
			if (valueGuid == null)
			{
				var serializedObject = wwiseObjectReferenceProperty.serializedObject;
				WwiseLogger.LogFormat(LogLevel.Log, "No data to migrate <{0}> on <{1}>.", wwiseObjectType, serializedObject.targetObject.GetType());
				return false;
			}

			var objectReference = WwiseObjectReference.GetWwiseObjectForMigration(wwiseObjectType, valueGuid, GetId(idProperty));
			return SetWwiseObjectReferenceProperty(wwiseObjectReferenceProperty, objectReference);
		}

		public static bool ProcessDoubleGuidType(UnityEditor.SerializedProperty wwiseObjectReferenceProperty, WwiseObjectType wwiseObjectType,
			UnityEditor.SerializedProperty valueGuidProperty, UnityEditor.SerializedProperty idProperty, 
			UnityEditor.SerializedProperty groupGuidProperty, UnityEditor.SerializedProperty groupIdProperty)
		{
			if (wwiseObjectReferenceProperty == null)
			{
				WwiseLogger.Error("This migration step is no longer necessary.");
				return false;
			}

			var valueGuid = GetByteArray(valueGuidProperty);
			var groupGuid = GetByteArray(groupGuidProperty);
			if (valueGuid == null || groupGuid == null)
			{
				var serializedObject = wwiseObjectReferenceProperty.serializedObject;
				WwiseLogger.LogFormat(LogLevel.Log, "No data to migrate <{0}> on <{1}>.", wwiseObjectType, serializedObject.targetObject.GetType());
				return false;
			}

			var objectReference = WwiseGroupValueObjectReference.GetWwiseObjectForMigration(wwiseObjectType, valueGuid, GetId(idProperty), groupGuid, GetId(groupIdProperty));
			return SetWwiseObjectReferenceProperty(wwiseObjectReferenceProperty, objectReference);
		}
	}
}

#endif // UNITY_EDITOR
#endif // #if !(UNITY_QNX) // Disable under unsupported platforms.
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

using System.Runtime.InteropServices;

/// <summary>
/// Represents a native array of AkChannelEmitters to be used with <see cref="AkUnitySoundEngine.SetMultiplePositions(UnityEngine.GameObject,AkChannelEmitterArray,ushort,AkMultiPositionType)"/>
/// </summary>
public class AkChannelEmitterArray : System.IDisposable
{
	/// <summary>
	/// C# representation of an AkChannelEmitter struct
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	private struct NativeAkChannelEmitter
	{
		/// Orientation of the emitter
		/// We are making the assumption throughout that sizeof(AkVector) == sizeof(UnityEngine.Vector3)
		public UnityEngine.Vector3 orientationFront;

		/// Top orientation of the emitter
		public UnityEngine.Vector3 orientationTop;

		/// Position of the emitter. Shouldn't use AkVector64 because it is a class...
		public double position_x;
		public double position_y;
		public double position_z;

		/// Channels to which the above position applies.
		public uint uInputChannels;

		/// In order to preserve consistent struct size across archs, we need some padding (4 bytes)
		private byte padding1;
		private byte padding2;
		private byte padding3;
		private byte padding4;
	}

	/// <summary>
	/// Pointer to the start of the unmanaged memory buffer.
	/// </summary>
	public System.IntPtr m_Buffer;

	private uint m_MaxCount;

	private static readonly int NativeStructSize = Marshal.SizeOf<NativeAkChannelEmitter>();

	private static readonly int OffFront = (int)Marshal.OffsetOf(typeof(NativeAkChannelEmitter), nameof(NativeAkChannelEmitter.orientationFront));
	private static readonly int OffTop   = (int)Marshal.OffsetOf(typeof(NativeAkChannelEmitter), nameof(NativeAkChannelEmitter.orientationTop));
	private static readonly int OffPosX  = (int)Marshal.OffsetOf(typeof(NativeAkChannelEmitter), nameof(NativeAkChannelEmitter.position_x));
	private static readonly int OffPosY  = (int)Marshal.OffsetOf(typeof(NativeAkChannelEmitter), nameof(NativeAkChannelEmitter.position_y));
	private static readonly int OffPosZ  = (int)Marshal.OffsetOf(typeof(NativeAkChannelEmitter), nameof(NativeAkChannelEmitter.position_z));
	private static readonly int OffMask  = (int)Marshal.OffsetOf(typeof(NativeAkChannelEmitter), nameof(NativeAkChannelEmitter.uInputChannels));

	public AkChannelEmitterArray(uint in_Count)
	{
		m_Buffer = Marshal.AllocHGlobal((int)(in_Count * NativeStructSize));
		m_MaxCount = in_Count;
		Count = 0;
	}

	/// <summary>
	/// Gets the number of emitters currently added to the array.
	/// </summary>
	public uint Count { get; private set; }

	public void Dispose()
	{

		System.GC.SuppressFinalize(this);

		if (m_Buffer != System.IntPtr.Zero)
		{
			Marshal.FreeHGlobal(m_Buffer);
			m_Buffer = System.IntPtr.Zero;
			m_MaxCount = 0;
		}
	}

	~AkChannelEmitterArray()
	{
		Dispose();
	}

	// <summary>
	/// Resets the iterator and the count to zero, allowing the array to be reused without reallocating memory.
	/// </summary>
	public void Reset()
	{
		Count = 0;
	}

	/// <summary>
	/// Adds a new emitter configuration to the array.
	/// </summary>
	/// <param name="in_Pos">The position of the emitter.</param>
	/// <param name="in_Forward">The forward orientation of the emitter.</param>
	/// <param name="in_Top">The top orientation of the emitter.</param>
	/// <param name="in_ChannelMask">The bitmask representing the channels, see <see cref="AkChannelConfig"/></param>
	/// <exception cref="System.IndexOutOfRangeException">Thrown if the array is already full.</exception>
	public void Add(AkVector64 in_Pos, UnityEngine.Vector3 in_Forward, UnityEngine.Vector3 in_Top,
		uint in_ChannelMask)
	{
		if (Count >= m_MaxCount)
			throw new System.IndexOutOfRangeException("Out of range access in AkChannelEmitterArray");

		// OPTIMIZATION. Since we don't use unsafe, and we want to avoid boxing via Marshal.StructureToPtr, the only solution is to write members individually.
		System.IntPtr pItem = m_Buffer + (int)(Count * NativeStructSize);

		// Orientation Front
		Marshal.WriteInt32(pItem, OffFront + 0, System.BitConverter.SingleToInt32Bits(in_Forward.x));
		Marshal.WriteInt32(pItem, OffFront + 4, System.BitConverter.SingleToInt32Bits(in_Forward.y));
		Marshal.WriteInt32(pItem, OffFront + 8, System.BitConverter.SingleToInt32Bits(in_Forward.z));

		// Orientation Top
		Marshal.WriteInt32(pItem, OffTop + 0, System.BitConverter.SingleToInt32Bits(in_Top.x));
		Marshal.WriteInt32(pItem, OffTop + 4, System.BitConverter.SingleToInt32Bits(in_Top.y));
		Marshal.WriteInt32(pItem, OffTop + 8, System.BitConverter.SingleToInt32Bits(in_Top.z));

		// Position - Direct 64-bit writes from the struct fields
		Marshal.WriteInt64(pItem, OffPosX, System.BitConverter.DoubleToInt64Bits(in_Pos.X));
		Marshal.WriteInt64(pItem, OffPosY, System.BitConverter.DoubleToInt64Bits(in_Pos.Y));
		Marshal.WriteInt64(pItem, OffPosZ, System.BitConverter.DoubleToInt64Bits(in_Pos.Z));

		// Mask
		Marshal.WriteInt32(pItem, OffMask, (int)in_ChannelMask);

		Count++;
	}
}
#endif // #if !(UNITY_QNX) // Disable under unsupported platforms.
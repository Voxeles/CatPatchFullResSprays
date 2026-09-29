using System;

namespace CatPatchFullResSprays;

internal sealed class FragmentBuffer
{
    public const int FragmentSize = 200 * 1024; // Steamworks has a 500MB hard limit
    public byte[] Bytes { get; }
    public int NextIndex { get; private set; }

    public int Count => (Bytes.Length + FragmentSize - 1) / FragmentSize;
    public bool IsComplete => NextIndex == Count;

    public FragmentBuffer(int size)
    {
        if (size <= 0 || size > SprayImage.HardByteSizeLimit)
            throw new ArgumentOutOfRangeException(nameof(size));
        Bytes = new byte[size];
    }

    public void Add(int index, byte[] fragment)
    {
        if (index < 0 || index >= Count || fragment == null || fragment.Length != Math.Min(FragmentSize, Bytes.Length - index * FragmentSize))
            throw new ArgumentException("Invalid fragment bounds.");
        if (index > NextIndex)
            throw new ArgumentException("Fragment arrived before its predecessor.");
        if (index < NextIndex)
            return;
        Buffer.BlockCopy(fragment, 0, Bytes, index * FragmentSize, fragment.Length);
        NextIndex++;
    }
}

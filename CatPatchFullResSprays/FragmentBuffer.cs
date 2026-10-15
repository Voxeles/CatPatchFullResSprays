using System;
using LiteNetLib.Utils;

namespace CatPatchFullResSprays;

internal sealed class FragmentBuffer
{
    public const int MinFragmentSize = 4 * 1024;
    public const int MaxFragmentSize = 256 * 1024; // Steam's message limit is 512KiB, no point in going that high though

    public byte[] Bytes { get; }
    public int FragmentSize { get; }
    public int Count { get; }
    public int ToReceiveIndex { get; private set; } // Every fragment below this index has arrived
    public int ReceivedCount { get; private set; }

    public bool IsComplete => ReceivedCount == Count;

    private readonly bool[] _receivedMap;

    public FragmentBuffer(int size, int fragmentSize)
    {
        if (size is <= 0 or > SprayImage.HardByteSizeLimit)
            throw new ArgumentOutOfRangeException(nameof(size));
        if (fragmentSize is < MinFragmentSize or > MaxFragmentSize)
            throw new ArgumentOutOfRangeException(nameof(fragmentSize));
        Bytes = new byte[size];
        FragmentSize = fragmentSize;
        Count = CountFor(size, fragmentSize);
        _receivedMap = new bool[Count];
    }

    public static int CountFor(int size, int fragmentSize) => (size + fragmentSize - 1) / fragmentSize;

    public int Length(int index) => Math.Min(FragmentSize, Bytes.Length - index * FragmentSize);

    public bool Add(int index, NetDataReader reader)
    {
        if (index < 0 || index >= Count || reader.AvailableBytes != Length(index))
            throw new ArgumentException("Invalid fragment bounds.");
        if (_receivedMap[index])
            return false;

        reader.GetBytes(Bytes, index * FragmentSize, Length(index));
        _receivedMap[index] = true;

        ReceivedCount++;
        while (ToReceiveIndex < Count && _receivedMap[ToReceiveIndex])
            ToReceiveIndex++;

        return true;
    }
}

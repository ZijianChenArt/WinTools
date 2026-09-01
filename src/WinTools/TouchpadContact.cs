using System;

namespace WinTools;

/// <summary>精确触控板的单个接触点。</summary>
internal readonly struct TouchpadContact : IEquatable<TouchpadContact>
{
    public int ContactId { get; }
    public int X { get; }
    public int Y { get; }

    public TouchpadContact(int contactId, int x, int y)
    {
        ContactId = contactId;
        X = x;
        Y = y;
    }

    public bool Equals(TouchpadContact other) => ContactId == other.ContactId && X == other.X && Y == other.Y;

    public override bool Equals(object? obj) => obj is TouchpadContact other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(ContactId, X, Y);
}

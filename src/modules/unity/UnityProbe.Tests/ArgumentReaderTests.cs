using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed unsafe class ArgumentReaderTests
{
	[Fact]
	public void NullPointerIsRejected()
	{
		Assert.False(ArgumentReader.TryRead(null, 4, out string? value));
		Assert.Null(value);
	}

	[Fact]
	public void NonPositiveLengthIsRejected()
	{
		char[] buffer = "hi".ToCharArray();
		fixed (char* pointer = buffer)
		{
			Assert.False(ArgumentReader.TryRead((ushort*)pointer, 0, out string? zero));
			Assert.Null(zero);
			Assert.False(ArgumentReader.TryRead((ushort*)pointer, -1, out string? negative));
			Assert.Null(negative);
		}
	}

	[Fact]
	public void ValidBufferIsDecoded()
	{
		char[] buffer = "hello".ToCharArray();
		fixed (char* pointer = buffer)
		{
			Assert.True(ArgumentReader.TryRead((ushort*)pointer, buffer.Length, out string? value));
			Assert.Equal("hello", value);
		}
	}

	[Fact]
	public void LengthBeyondUtf16MaximumIsRejected()
	{
		// A legitimately allocated buffer; the guard must reject the length before
		// the copy, because a length above 32767 UTF-16 code units cannot name a
		// Windows extended-length path and must never reach `new string`.
		char[] buffer = new char[32768];
		buffer[0] = 'x';
		fixed (char* pointer = buffer)
		{
			Assert.False(ArgumentReader.TryRead((ushort*)pointer, buffer.Length, out string? value));
			Assert.Null(value);
		}
	}

	[Fact]
	public void MaximumLengthIsAccepted()
	{
		char[] buffer = new char[ArgumentReader.MaxLength];
		for (int i = 0; i < buffer.Length; i++)
		{
			buffer[i] = 'a';
		}

		fixed (char* pointer = buffer)
		{
			Assert.True(ArgumentReader.TryRead((ushort*)pointer, buffer.Length, out string? value));
			Assert.Equal(ArgumentReader.MaxLength, value!.Length);
		}
	}

	[Fact]
	public void ImpossibleLengthIsRejectedWithoutDereferencing()
	{
		// A hostile caller can pair a tiny valid buffer with int.MaxValue length. The
		// length guard must reject it before `new string` ever runs.
		char[] buffer = "hi".ToCharArray();
		fixed (char* pointer = buffer)
		{
			Assert.False(ArgumentReader.TryRead((ushort*)pointer, int.MaxValue, out string? value));
			Assert.Null(value);
		}
	}
}

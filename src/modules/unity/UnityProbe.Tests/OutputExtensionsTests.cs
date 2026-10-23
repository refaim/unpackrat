using Xunit;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>
/// The extension normaliser and the video-container allow-list: every accepted value stays inside
/// the ABI alphabet and length, and any unrecognised or hostile candidate becomes the safe bin
/// fallback rather than a real binary extension.
/// </summary>
public sealed class OutputExtensionsTests
{
	[Theory]
	[InlineData(null, "bin")]
	[InlineData("", "bin")]
	[InlineData("png", "png")]
	[InlineData("0123456789abcdef", "0123456789abcdef")]
	[InlineData("0123456789abcdefg", "bin")]
	[InlineData("MP4", "bin")]
	[InlineData("a-b", "bin")]
	public void NormaliseEnforcesTheAbiAlphabetAndLength(string? candidate, string expected)
		=> Assert.Equal(expected, OutputExtensions.Normalise(candidate));

	[Theory]
	[InlineData(null, "bin")]
	[InlineData("mp4", "mp4")]
	[InlineData("webm", "webm")]
	[InlineData("exe", "bin")]
	[InlineData("MP4", "bin")]
	public void VideoOnlyAcceptsRecognisedMediaContainers(string? fromPath, string expected)
		=> Assert.Equal(expected, OutputExtensions.Video(fromPath));
}

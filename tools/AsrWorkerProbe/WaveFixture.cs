using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace LiveCaptionsTranslator.tools.asrworkerprobe;

internal sealed record WaveFixture(byte[] Pcm, int PaddedBytes)
{
    public int FrameCount => Pcm.Length / 640;
}

internal static class WaveFixtureReader
{
    public static WaveFixture Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") throw new InvalidDataException("WAV must begin with RIFF.");
        _ = reader.ReadUInt32();
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") throw new InvalidDataException("RIFF form type must be WAVE.");
        ushort? format = null, channels = null, bits = null;
        uint? sampleRate = null;
        byte[]? data = null;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var size = reader.ReadUInt32();
            if (size > int.MaxValue || stream.Position + size > stream.Length) throw new InvalidDataException("WAV chunk length is invalid.");
            if (id == "fmt ")
            {
                if (size < 16) throw new InvalidDataException("WAV fmt chunk is too short.");
                format = reader.ReadUInt16(); channels = reader.ReadUInt16(); sampleRate = reader.ReadUInt32();
                _ = reader.ReadUInt32(); _ = reader.ReadUInt16(); bits = reader.ReadUInt16();
                stream.Position += size - 16;
            }
            else if (id == "data") data = reader.ReadBytes((int)size);
            else stream.Position += size;
            if ((size & 1) != 0 && stream.Position < stream.Length) stream.Position++;
        }
        if (format != 1) throw new InvalidDataException("WAV format must be PCM format 1.");
        if (sampleRate != 16000) throw new InvalidDataException("WAV sample rate must be 16000 Hz.");
        if (channels != 1) throw new InvalidDataException("WAV must be mono.");
        if (bits != 16) throw new InvalidDataException("WAV must use 16-bit little-endian samples.");
        if (data == null || data.Length == 0) throw new InvalidDataException("WAV data chunk is missing or empty.");
        if ((data.Length & 1) != 0) throw new InvalidDataException("WAV PCM data must contain complete 16-bit samples.");
        var padded = (640 - data.Length % 640) % 640;
        if (padded == 0) return new(data, 0);
        Array.Resize(ref data, data.Length + padded);
        return new(data, padded);
    }
}

internal static class ExpectedCaptionMatcher
{
    public static bool Contains(string transcript, string expected)
    {
        var normalizedTranscript = Normalize(transcript);
        var normalizedExpected = Normalize(expected);
        return normalizedExpected.Length != 0 && normalizedTranscript.Contains(normalizedExpected, StringComparison.Ordinal);
    }

    private static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture);
        var result = new StringBuilder(); var space = false;
        foreach (var character in decomposed)
        {
            if (char.IsLetterOrDigit(character)) { if (space && result.Length != 0) result.Append(' '); result.Append(character); space = false; }
            else if (char.IsWhiteSpace(character) || char.IsPunctuation(character)) space = true;
        }
        return result.ToString();
    }
}

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Managerr.Common.Paths;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Whisper.net;
using Whisper.net.Ggml;

namespace Managerr.Verification;

/// <summary>
/// Thread-safe on-demand manager and CPU inference runner for Stage 3 neural audio verification models:
/// 1. Silero VAD v5.1.2 (<c>silero_vad.onnx</c>, ~2.3 MB, MIT) via <see cref="InferenceSession"/>.
/// 2. OpenAI Whisper Tiny Q5_1 (<c>ggml-tiny-q5_1.bin</c>, ~32.2 MB, MIT) via <see cref="WhisperFactory"/>.
/// </summary>
public sealed class VerificationModelManager
{
    public const string SileroVadFileName = "silero_vad.onnx";
    public const string WhisperTinyFileName = "ggml-tiny-q5_1.bin";
    public const string SileroVadDownloadUrl = "https://raw.githubusercontent.com/snakers4/silero-vad/v5.1.2/src/silero_vad/data/silero_vad.onnx";
    public const string SileroVadExpectedSha256 = "2623a2953f6ff3d2c1e61740c6cdb7168133479b267dfef114a4a3cc5bdd788f";
    private const long MinWhisperTinyBytes = 25_000_000;

    private static readonly SemaphoreSlim ModelInitLock = new(1, 1);
    private static readonly HttpClient ModelDownloadHttp = CreateModelDownloadHttpClient();
    private static readonly ConcurrentDictionary<string, InferenceSession> SharedVadSessions = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, WhisperRuntimeHolder> SharedWhisperRuntimes = new(StringComparer.Ordinal);

    private readonly ILogger? _logger;

    public VerificationModelManager(
        IAppPaths? appPaths = null,
        IConfiguration? configuration = null,
        ILogger? logger = null,
        string? modelsDirectoryOverride = null)
    {
        _logger = logger;

        var configuredDir = modelsDirectoryOverride;
        if (string.IsNullOrWhiteSpace(configuredDir))
        {
            configuredDir = configuration?["Managerr:ModelsDirectory"];
        }

        if (string.IsNullOrWhiteSpace(configuredDir))
        {
            configuredDir = Environment.GetEnvironmentVariable("MANAGERR_MODELS_DIR");
        }

        if (string.IsNullOrWhiteSpace(configuredDir))
        {
            configuredDir = appPaths is not null
                ? Path.Combine(appPaths.ConfigDirectory, "models")
                : Path.Combine(Path.GetTempPath(), "managerr-models-cache");
        }

        ModelsDirectory = Path.GetFullPath(configuredDir);
        Directory.CreateDirectory(ModelsDirectory);
    }

    public string ModelsDirectory { get; }
    public string SileroVadModelPath => Path.Combine(ModelsDirectory, SileroVadFileName);
    public string WhisperTinyModelPath => Path.Combine(ModelsDirectory, WhisperTinyFileName);

    public async Task<string> EnsureSileroVadModelAsync(CancellationToken ct = default)
    {
        var targetPath = SileroVadModelPath;
        if (IsValidSileroVadFile(targetPath))
        {
            return targetPath;
        }

        await ModelInitLock.WaitAsync(ct);
        try
        {
            if (IsValidSileroVadFile(targetPath))
            {
                return targetPath;
            }

            Directory.CreateDirectory(ModelsDirectory);
            var tmpPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                _logger?.LogInformation("Downloading Silero VAD v5.1.2 ONNX model to {Path}...", targetPath);
                using var resp = await ModelDownloadHttp.GetAsync(SileroVadDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();

                await using (var fs = File.Create(tmpPath))
                {
                    await resp.Content.CopyToAsync(fs, ct);
                }

                if (!IsValidSileroVadFile(tmpPath))
                {
                    throw new InvalidDataException($"Downloaded Silero VAD ONNX model failed SHA-256 ({SileroVadExpectedSha256}) or protobuf header verification.");
                }

                File.Move(tmpPath, targetPath, overwrite: true);
                return targetPath;
            }
            finally
            {
                TryDeleteFile(tmpPath);
            }
        }
        finally
        {
            ModelInitLock.Release();
        }
    }

    public async Task<string> EnsureWhisperTinyModelAsync(CancellationToken ct = default)
    {
        var targetPath = WhisperTinyModelPath;
        if (IsValidWhisperGgmlFile(targetPath))
        {
            return targetPath;
        }

        await ModelInitLock.WaitAsync(ct);
        try
        {
            if (IsValidWhisperGgmlFile(targetPath))
            {
                return targetPath;
            }

            Directory.CreateDirectory(ModelsDirectory);
            var tmpPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                _logger?.LogInformation("Downloading Whisper GGML Tiny Q5_1 model to {Path}...", targetPath);
                await using (var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.Tiny, QuantizationType.Q5_1, ct))
                await using (var fs = File.Create(tmpPath))
                {
                    await modelStream.CopyToAsync(fs, ct);
                }

                if (!HasValidGgmlHeaderAndSize(tmpPath, out var computedSha256))
                {
                    throw new InvalidDataException("Downloaded Whisper GGML Tiny Q5_1 model failed magic-byte (0x67676d6c) or size sanity check.");
                }

                File.Move(tmpPath, targetPath, overwrite: true);
                await File.WriteAllTextAsync($"{targetPath}.sha256", computedSha256, ct);
                return targetPath;
            }
            finally
            {
                TryDeleteFile(tmpPath);
            }
        }
        finally
        {
            ModelInitLock.Release();
        }
    }

    /// <summary>
    /// Runs Silero VAD v5 on 16 kHz mono float32 PCM samples using 512-sample (32ms) frames + 64-sample context.
    /// Returns total speech duration in seconds and contiguous speech-active PCM (with 256ms padding around speech islands).
    /// </summary>
    public async Task<(double SpeechSec, double MaxSpeechProb, float[] SpeechPcm16k)> MeasureSpeechActivityAsync(
        float[] pcm16k,
        CancellationToken ct = default)
    {
        if (pcm16k.Length < 512)
        {
            return (0.0, 0.0, []);
        }

        var vadPath = await EnsureSileroVadModelAsync(ct);
        var session = SharedVadSessions.GetOrAdd(vadPath, static path =>
        {
            var opts = new SessionOptions
            {
                InterOpNumThreads = 1,
                IntraOpNumThreads = 1,
            };
            return new InferenceSession(path, opts);
        });

        const int frameSize = 512;
        const int contextSize = 64;
        var frameCount = pcm16k.Length / frameSize;
        var frameProbs = new float[frameCount];
        var state = new float[2 * 1 * 128];
        var context = new float[contextSize];
        var inputBuffer = new float[contextSize + frameSize];
        var srTensor = new DenseTensor<long>(new long[] { 16000L }, ReadOnlySpan<int>.Empty);
        var maxProb = 0.0;

        for (var f = 0; f < frameCount; f++)
        {
            ct.ThrowIfCancellationRequested();
            Array.Copy(context, 0, inputBuffer, 0, contextSize);
            Array.Copy(pcm16k, f * frameSize, inputBuffer, contextSize, frameSize);

            var inputTensor = new DenseTensor<float>(inputBuffer, new[] { 1, contextSize + frameSize });
            var stateTensor = new DenseTensor<float>(state, new[] { 2, 1, 128 });

            using var results = session.Run(
            [
                NamedOnnxValue.CreateFromTensor("input", inputTensor),
                NamedOnnxValue.CreateFromTensor("state", stateTensor),
                NamedOnnxValue.CreateFromTensor("sr", srTensor),
            ]);

            var prob = 0f;
            foreach (var r in results)
            {
                if (string.Equals(r.Name, "output", StringComparison.Ordinal))
                {
                    prob = r.AsTensor<float>()[0, 0];
                }
                else if (string.Equals(r.Name, "stateN", StringComparison.Ordinal))
                {
                    var nextState = r.AsEnumerable<float>().ToArray();
                    if (nextState.Length == state.Length)
                    {
                        Array.Copy(nextState, state, state.Length);
                    }
                }
            }

            Array.Copy(inputBuffer, frameSize, context, 0, contextSize);
            frameProbs[f] = prob;
            if (prob > maxProb)
            {
                maxProb = prob;
            }
        }

        // Count active speech frames (prob >= 0.45) and include 8-frame (256ms) lead-in/hangover padding
        // so word-initial/final consonants and natural inter-word prosody stay intact for Whisper.
        var speechFrames = 0;
        var keepFrame = new bool[frameCount];
        const int padFrames = 8;

        for (var f = 0; f < frameCount; f++)
        {
            if (frameProbs[f] >= 0.45f)
            {
                speechFrames++;
                var start = Math.Max(0, f - padFrames);
                var end = Math.Min(frameCount - 1, f + padFrames);
                for (var k = start; k <= end; k++)
                {
                    keepFrame[k] = true;
                }
            }
        }

        var speechSec = Math.Round(speechFrames * (frameSize / 16000.0), 3);
        if (speechFrames == 0)
        {
            return (0.0, Math.Round(maxProb, 3), []);
        }

        var keptCount = keepFrame.Count(x => x);
        var speechPcm = new float[keptCount * frameSize];
        var dstOffset = 0;
        for (var f = 0; f < frameCount; f++)
        {
            if (keepFrame[f])
            {
                Array.Copy(pcm16k, f * frameSize, speechPcm, dstOffset, frameSize);
                dstOffset += frameSize;
            }
        }

        return (speechSec, Math.Round(maxProb, 3), speechPcm);
    }

    /// <summary>
    /// Runs CPU Whisper language identification on a 16 kHz mono float32 PCM speech buffer.
    /// </summary>
    public async Task<(string Language, double Probability)> DetectSpokenLanguageAsync(
        float[] speechPcm16k,
        CancellationToken ct = default)
    {
        if (speechPcm16k.Length < 8_000)
        {
            return ("und", 0.0);
        }

        var whisperPath = await EnsureWhisperTinyModelAsync(ct);
        var holder = SharedWhisperRuntimes.GetOrAdd(whisperPath, static path => new WhisperRuntimeHolder(path));
        return await holder.DetectLanguageAsync(speechPcm16k, ct);
    }

    public static bool IsValidSileroVadFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return false;
            }

            var fi = new FileInfo(filePath);
            if (fi.Length < 1_000_000)
            {
                return false;
            }

            using var fs = File.OpenRead(filePath);
            var firstByte = fs.ReadByte();
            if (firstByte != 0x08) // ONNX protobuf ir_version field tag
            {
                return false;
            }

            fs.Position = 0;
            var shaHex = Convert.ToHexStringLower(SHA256.HashData(fs));
            return string.Equals(shaHex, SileroVadExpectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsValidWhisperGgmlFile(string filePath)
    {
        try
        {
            if (!HasValidGgmlHeaderAndSize(filePath, out var actualSha))
            {
                return false;
            }

            var sidecar = $"{filePath}.sha256";
            if (File.Exists(sidecar))
            {
                var expected = File.ReadAllText(sidecar).Trim();
                if (!string.IsNullOrWhiteSpace(expected) &&
                    !string.Equals(expected, actualSha, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasValidGgmlHeaderAndSize(string filePath, out string sha256Hex)
    {
        sha256Hex = "";
        if (!File.Exists(filePath))
        {
            return false;
        }

        var fi = new FileInfo(filePath);
        if (fi.Length < MinWhisperTinyBytes)
        {
            return false;
        }

        using var fs = File.OpenRead(filePath);
        Span<byte> magic = stackalloc byte[4];
        if (fs.Read(magic) < 4)
        {
            return false;
        }

        // GGML magic 0x67676d6c in little-endian: 0x6C ('l'), 0x6D ('m'), 0x67 ('g'), 0x67 ('g')
        if (magic[0] != 0x6C || magic[1] != 0x6D || magic[2] != 0x67 || magic[3] != 0x67)
        {
            return false;
        }

        fs.Position = 0;
        sha256Hex = Convert.ToHexStringLower(SHA256.HashData(fs));
        return true;
    }

    private static HttpClient CreateModelDownloadHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(3),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Managerr-Verification/1.0");
        return client;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore best-effort cleanup
        }
    }

    private sealed class WhisperRuntimeHolder
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly WhisperFactory _factory;
        private readonly WhisperProcessor _processor;

        public WhisperRuntimeHolder(string modelPath)
        {
            _factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = false });
            _processor = _factory.CreateBuilder()
                .WithLanguage("auto")
                .WithThreads(Math.Clamp(Environment.ProcessorCount, 1, 4))
                .Build();
        }

        public async Task<(string Language, double Probability)> DetectLanguageAsync(float[] pcm16k, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                var (rawLang, prob) = _processor.DetectLanguageWithProbability(pcm16k);
                var normLang = LanguageCodeNormalizer.NormalizeToIso2(rawLang);
                if (normLang == "und" || float.IsNaN(prob) || prob <= 0f)
                {
                    return ("und", 0.0);
                }

                return (normLang, Math.Round(Math.Clamp((double)prob, 0.0, 0.999), 4));
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}

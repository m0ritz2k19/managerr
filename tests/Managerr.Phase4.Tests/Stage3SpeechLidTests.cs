using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Managerr.Datastore;
using Managerr.Verification;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Managerr.Phase4.Tests;

public sealed class Stage3SpeechLidTests
{
    private static readonly string SharedModelsCacheDir =
        Environment.GetEnvironmentVariable("MANAGERR_MODELS_DIR")
        ?? Path.Combine(Path.GetTempPath(), "managerr-models-cache");

    [Fact]
    public async Task UntaggedGermanSpeechMkv_AutoTagsViaMkvHeaderPatcher_AndVerifiesDeuTagWithFfprobe()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var factory = CreateWebFactory(tempDir);
            using var scope = factory.Services.CreateScope();
            var verifier = scope.ServiceProvider.GetRequiredService<ILanguageVerificationService>();
            var dbFactory = scope.ServiceProvider.GetRequiredService<ISqliteDbFactory>();

            // Build real Matroska (.mkv) containing spoken German dialogue tagged as 'und' with neutral title 'Audio Track 1'
            // proving neural Silero VAD + Whisper LID detects German from speech alone (never relying on title hints!)
            var mkvPath = Path.Combine(tempDir, "Shadow.Protocol.2024.German.1080p.WEB.H264-AUTOPATCH.mkv");
            await BuildRealSpeechMkvAsync(
                mkvPath,
                [(GetFixturePath("speech-de.opus"), "und", "Audio Track 1")]);

            Assert.Equal("und", await ReadFirstAudioStreamLanguageViaFfprobeAsync(mkvPath));

            var report = await verifier.VerifyStagedFileAsync(new RunVerificationRequest(
                StagedFilePath: mkvPath,
                MediaVertical: "movies",
                ReleaseTitle: "Shadow.Protocol.2024.German.1080p.WEB.H264-AUTOPATCH",
                OriginalLanguage: "de",
                ExpectedAudioLanguages: ["de"]));

            Assert.Equal(VerificationVerdicts.VerifiedPassAutoTagged, report.Verdict);
            Assert.True(report.HeaderAutoPatched);
            Assert.Contains("de", report.VerifiedAudioLanguages);

            var audioStream = Assert.Single(report.Streams, s => s.StreamType == "audio");
            Assert.Equal("und", audioStream.DeclaredLanguage);
            Assert.Equal("de", audioStream.DetectedLanguage);
            Assert.True(audioStream.Confidence >= 0.85, $"Expected high Whisper confidence >= 0.85, got {audioStream.Confidence}");
            Assert.Equal("deu", audioStream.AppliedTagPatch);
            Assert.Equal("silero-vad+whisper-ggml", audioStream.Method);
            Assert.NotNull(audioStream.Windows);
            Assert.Contains(audioStream.Windows!, w => w.SpeechSec >= 1.2 && w.DetectedLanguage == "de");

            // Confirm the physical MKV container track tag on disk was updated from 'und' -> 'deu'/'ger' via ffprobe!
            var patchedIso3 = await ReadFirstAudioStreamLanguageViaFfprobeAsync(mkvPath);
            Assert.Contains(patchedIso3, new[] { "deu", "ger" });

            // Confirm no raw float[] PCM samples leaked into stored verification_jobs.report_json
            await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core);
            var storedJson = await conn.ExecuteScalarAsync<string>(
                "SELECT report_json FROM verification_jobs WHERE id = @Id;",
                new { Id = report.JobId });
            Assert.DoesNotContain("pcmFingerprint", storedJson!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("speechWindows16k", storedJson!, StringComparison.OrdinalIgnoreCase);
            Assert.True(storedJson!.Length < 10_000, $"Expected compact report_json, got {storedJson.Length} bytes");
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task EnglishSpeechMkvMislabeledAsGerman_FailsVerification_AndBlocklistsRelease()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var factory = CreateWebFactory(tempDir);
            using var scope = factory.Services.CreateScope();
            var verifier = scope.ServiceProvider.GetRequiredService<ILanguageVerificationService>();

            var mislabeledMkv = Path.Combine(tempDir, "Fake.German.Release.2025.1080p.WEB-DL-LIAR.mkv");
            await BuildRealSpeechMkvAsync(
                mislabeledMkv,
                [(GetFixturePath("speech-en.opus"), "deu", "Deutsch DD+ 5.1")]);

            // 1. Profile expects German ('de'), container claims 'deu' + 'Deutsch DD+ 5.1', but Whisper hears English!
            var expectedDeReport = await verifier.VerifyStagedFileAsync(new RunVerificationRequest(
                StagedFilePath: mislabeledMkv,
                MediaVertical: "movies",
                ReleaseTitle: "Fake.German.Release.2025.1080p.WEB-DL-LIAR",
                InfoHash: "aa11bb22cc33dd44ee55ff6677889900aabbccdd",
                ExpectedAudioLanguages: ["de"]));

            Assert.Equal(VerificationVerdicts.VerifiedFail, expectedDeReport.Verdict);
            Assert.True(expectedDeReport.HashBlocklisted);
            Assert.True(expectedDeReport.ExistingFilePreservedOnFailure);
            Assert.DoesNotContain("de", expectedDeReport.VerifiedAudioLanguages);
            Assert.Contains("en", expectedDeReport.VerifiedAudioLanguages);

            var stream = Assert.Single(expectedDeReport.Streams);
            Assert.Equal("de", stream.DeclaredLanguage);
            Assert.Equal("en", stream.DetectedLanguage);
            Assert.True(stream.Confidence >= 0.85, $"Expected Whisper English confidence >= 0.85, got {stream.Confidence}");

            // 2. Standalone scan with empty ExpectedAudioLanguages also catches explicit tag contradiction ('de' vs 'en')
            var standaloneReport = await verifier.VerifyStagedFileAsync(new RunVerificationRequest(
                StagedFilePath: mislabeledMkv,
                MediaVertical: "movies",
                ReleaseTitle: "Fake.German.Standalone.Scan"));
            Assert.Equal(VerificationVerdicts.VerifiedFail, standaloneReport.Verdict);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task AmbiguousToneUndMkv_EvenWithMisleadingTitleHint_LandsInQuarantineReview()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var factory = CreateWebFactory(tempDir);
            using var scope = factory.Services.CreateScope();
            var verifier = scope.ServiceProvider.GetRequiredService<ILanguageVerificationService>();

            // Build 10-second 580 Hz non-speech tone MKV with 'und' tag AND a lying 'Deutsch DD5.1' title hint:
            // Silero VAD detects 0.0s human speech, so Stage 3 refuses to auto-tag from titleHint and quarantines!
            var toneMkv = Path.Combine(tempDir, "Shadow.Protocol.2024.German.1080p.WEB.H264-UNDTAG.mkv");
            await BuildToneMkvAsync(toneMkv, durationSec: 10, frequencyHz: 580, language: "und", title: "Deutsch DD5.1");

            var report = await verifier.VerifyStagedFileAsync(new RunVerificationRequest(
                StagedFilePath: toneMkv,
                MediaVertical: "movies",
                ReleaseTitle: "Shadow.Protocol.2024.German.1080p.WEB.H264-UNDTAG",
                InfoHash: "nzo:undtag-tone-test",
                ExpectedAudioLanguages: ["de"]));

            Assert.Equal(VerificationVerdicts.QuarantineReview, report.Verdict);
            Assert.False(report.HeaderAutoPatched);
            Assert.NotNull(report.QuarantineId);

            var stream = Assert.Single(report.Streams);
            Assert.Equal("und", stream.DetectedLanguage);
            Assert.All(stream.Windows!, w => Assert.True(w.SpeechSec < 1.2, $"Expected < 1.2s speech on pure sine wave, got {w.SpeechSec}"));

            var quarantined = Assert.Single(await verifier.ListQuarantineAsync("quarantined"));
            Assert.Equal(report.QuarantineId, quarantined.Id);
            Assert.NotEmpty(quarantined.SnippetPaths);
            Assert.All(quarantined.SnippetPaths, p => Assert.True(File.Exists(p)));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task GenuineDualAudioSpeechMkv_Passes_WhileIdenticalSpokenFakeDubMkv_Fails()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var factory = CreateWebFactory(tempDir);
            using var scope = factory.Services.CreateScope();
            var verifier = scope.ServiceProvider.GetRequiredService<ILanguageVerificationService>();

            var deOpus = GetFixturePath("speech-de.opus");
            var enOpus = GetFixturePath("speech-en.opus");

            // 1. Genuine Dual-Audio (Track 1 real German speech + Track 2 real English speech) -> VerifiedPass!
            var genuineDualMkv = Path.Combine(tempDir, "Neon.Horizon.2025.German.DL.1080p.BluRay.x265-TESTLAB.mkv");
            await BuildRealSpeechMkvAsync(
                genuineDualMkv,
                [
                    (deOpus, "deu", "Deutsch DD+ 5.1"),
                    (enOpus, "eng", "English DD+ 5.1"),
                ]);

            var dualPass = await verifier.VerifyStagedFileAsync(new RunVerificationRequest(
                StagedFilePath: genuineDualMkv,
                MediaVertical: "movies",
                ReleaseTitle: "Neon.Horizon.2025.German.DL.1080p.BluRay.x265-TESTLAB",
                ExpectedAudioLanguages: ["de", "en"]));

            Assert.Equal(VerificationVerdicts.VerifiedPass, dualPass.Verdict);
            Assert.Contains("de", dualPass.VerifiedAudioLanguages);
            Assert.Contains("en", dualPass.VerifiedAudioLanguages);
            Assert.All(dualPass.Streams, s => Assert.False(s.IsDuplicateFakeDub));

            // 2. Fake Duplicate Dub (identical English speech duplicated onto both 'eng' and 'deu' tracks) -> VerifiedFail!
            var fakeDubMkv = Path.Combine(tempDir, "Neon.Horizon.2025.German.FAKE.DUB.1080p.WEB-BADGRP.mkv");
            await BuildRealSpeechMkvAsync(
                fakeDubMkv,
                [
                    (enOpus, "eng", "English Original"),
                    (enOpus, "deu", "German Dub"),
                ]);

            var fakeFail = await verifier.VerifyStagedFileAsync(new RunVerificationRequest(
                StagedFilePath: fakeDubMkv,
                MediaVertical: "movies",
                ReleaseTitle: "Neon.Horizon.2025.German.FAKE.DUB.1080p.WEB-BADGRP",
                ExpectedAudioLanguages: ["de", "en"]));

            Assert.Equal(VerificationVerdicts.VerifiedFail, fakeFail.Verdict);
            Assert.Contains(fakeFail.Streams, s => s.IsDuplicateFakeDub);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Stage2LinguaTextDetector_AndModelManagerIntegrity_VerifyLanguagesAndChecksums()
    {
        var detector = new StatisticalTextLanguageDetector();

        var samples = new (string ExpectedIso2, string Text)[]
        {
            ("de", "Wir müssen die Verbindung sofort wiederherstellen, bevor das Signal der Forschungsstation endgültig verloren geht."),
            ("en", "All primary bridge security systems aboard the deep ocean research station are operating normally tonight."),
            ("fr", "Nous devons absolument rétablir la communication avec le navire avant que la tempête n'atteigne la côte."),
            ("es", "Tenemos que restablecer la conexión de inmediato antes de que la tormenta sobre la ciudad sea demasiado fuerte."),
            ("ja", "嵐が街に到達する前に、私たちは今すぐ研究ステーションとの通信を回復しなければなりません。"),
        };

        foreach (var (expected, text) in samples)
        {
            var (detected, confidence) = detector.IdentifyTextLanguage(text);
            Assert.Equal(expected, detected);
            Assert.True(confidence >= 0.90, $"Expected Lingua confidence >= 0.90 for '{expected}', got {confidence}");
        }

        // LanguageCodeNormalizer titleHint diagnostics vs raw 'und'
        Assert.Equal("de", LanguageCodeNormalizer.NormalizeToIso2("und", "Deutsch DD5.1"));
        Assert.Equal("en", LanguageCodeNormalizer.NormalizeToIso2("und", "English AAC 2.0"));
        Assert.Equal("und", LanguageCodeNormalizer.NormalizeToIso2("und", "Audio Track 1"));
        Assert.Equal("de", LanguageCodeNormalizer.TryInferLanguageFromTitleHint("German TrueHD Atmos"));
        Assert.Null(LanguageCodeNormalizer.TryInferLanguageFromTitleHint("Audio Track 1"));

        // VerificationModelManager ensures both neural models exist in SharedModelsCacheDir & pass header/SHA-256 checks
        var mgr = new VerificationModelManager(modelsDirectoryOverride: SharedModelsCacheDir);
        var vadPath = await mgr.EnsureSileroVadModelAsync();
        var whisperPath = await mgr.EnsureWhisperTinyModelAsync();

        Assert.Equal(Path.GetFullPath(SharedModelsCacheDir), mgr.ModelsDirectory);
        Assert.True(VerificationModelManager.IsValidSileroVadFile(vadPath));
        Assert.True(VerificationModelManager.IsValidWhisperGgmlFile(whisperPath));
    }

    private static WebApplicationFactory<Program> CreateWebFactory(string tempDir) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Managerr:ConfigDirectory"] = tempDir,
                    ["Managerr:ModelsDirectory"] = SharedModelsCacheDir,
                    ["Managerr:MasterKey"] = "stage3-speech-lid-master-key-32b!",
                    ["Managerr:SchedulerEnabled"] = "false",
                });
            });
        });

    private static string GetFixturePath(string fileName)
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        throw new FileNotFoundException($"Missing audio test fixture: {candidate}");
    }

    private static async Task BuildRealSpeechMkvAsync(
        string outputMkvPath,
        IReadOnlyList<(string AudioInputPath, string LanguageIso3, string Title)> audioTracks)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        var args = proc.StartInfo.ArgumentList;
        args.Add("-y");
        args.Add("-v");
        args.Add("error");
        args.Add("-f");
        args.Add("lavfi");
        args.Add("-i");
        args.Add("color=c=black:s=320x180:r=10:d=8");

        foreach (var (audioPath, _, _) in audioTracks)
        {
            args.Add("-i");
            args.Add(audioPath);
        }

        args.Add("-map");
        args.Add("0:v:0");
        for (var i = 0; i < audioTracks.Count; i++)
        {
            args.Add("-map");
            args.Add($"{i + 1}:a:0");
        }

        args.Add("-c:v");
        args.Add("libx264");
        args.Add("-preset");
        args.Add("ultrafast");
        args.Add("-crf");
        args.Add("40");
        args.Add("-pix_fmt");
        args.Add("yuv420p");

        for (var i = 0; i < audioTracks.Count; i++)
        {
            var (_, lang, title) = audioTracks[i];
            args.Add($"-c:a:{i}");
            args.Add("aac");
            args.Add($"-b:a:{i}");
            args.Add("96k");
            args.Add($"-metadata:s:a:{i}");
            args.Add($"language={lang}");
            args.Add($"-metadata:s:a:{i}");
            args.Add($"title={title}");
        }

        args.Add("-shortest");
        args.Add(outputMkvPath);

        proc.Start();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        Assert.True(proc.ExitCode == 0, $"ffmpeg BuildRealSpeechMkvAsync failed ({proc.ExitCode}): {stderr}");
    }

    private static async Task BuildToneMkvAsync(
        string outputMkvPath,
        int durationSec,
        int frequencyHz,
        string language,
        string title)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                ArgumentList =
                {
                    "-y", "-v", "error",
                    "-f", "lavfi", "-i", $"color=c=black:s=320x180:r=10:d={durationSec}",
                    "-f", "lavfi", "-i", $"sine=frequency={frequencyHz}:sample_rate=16000:duration={durationSec}",
                    "-map", "0:v:0", "-map", "1:a:0",
                    "-c:v", "libx264", "-preset", "ultrafast", "-crf", "40", "-pix_fmt", "yuv420p",
                    "-c:a:0", "aac", "-b:a:0", "64k",
                    "-metadata:s:a:0", $"language={language}",
                    "-metadata:s:a:0", $"title={title}",
                    outputMkvPath,
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        proc.Start();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        Assert.True(proc.ExitCode == 0, $"ffmpeg BuildToneMkvAsync failed ({proc.ExitCode}): {stderr}");
    }

    private static async Task<string> ReadFirstAudioStreamLanguageViaFfprobeAsync(string mkvPath)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffprobe",
                ArgumentList = { "-v", "quiet", "-print_format", "json", "-show_streams", "-select_streams", "a:0", mkvPath },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        proc.Start();
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        Assert.Equal(0, proc.ExitCode);

        using var doc = JsonDocument.Parse(stdout);
        var stream = doc.RootElement.GetProperty("streams")[0];
        if (stream.TryGetProperty("tags", out var tags))
        {
            if (tags.TryGetProperty("language", out var l)) return l.GetString() ?? "und";
            if (tags.TryGetProperty("LANGUAGE", out var lu)) return lu.GetString() ?? "und";
        }

        return "und";
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"managerr-stage3-lid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Ignore cleanup race
        }
    }
}

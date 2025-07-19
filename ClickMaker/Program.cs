using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

const string midiDir = @"D:\Desktop\ClickCreate";
const string primaryClickPath = "ClickPrimary.wav";
const string secondaryClickPath = "ClickSecondary.wav";

foreach (var file in Directory.GetFiles(
             midiDir,
             "*.mid",
             SearchOption.TopDirectoryOnly
         ))
{
    var clicks = CreateClickInfo(file);
    CreateAudioTrack(
        CollectionsMarshal.AsSpan(clicks),
        Path.ChangeExtension(file, "click.wav"),
        primaryClickPath,
        secondaryClickPath
     );
}

return;

static List<ClickInfo> CreateClickInfo(string midiFilePath)
{
    var sourceMidiFile = MidiFile.Read(midiFilePath);
    var sourceTempoMap = sourceMidiFile.GetTempoMap();

    var maxMidiClicks = sourceMidiFile.Chunks
        .OfType<TrackChunk>()
        .SelectMany(trackChunk => trackChunk.GetNotes())
        .Max(note => note.Time);

    var timeSigChange = sourceTempoMap
        .GetTimeSignatureChanges()
        .Select(x => new TimeSignatureEvent(x.Time, x.Value))
        .ToList();

    var tempoChange = sourceTempoMap
        .GetTempoChanges()
        .Select(x => new TempoEvent(x.Time, x.Value))
        .ToArray();

    if (timeSigChange.Count == 0 || timeSigChange[0].MidiTime != 0)
        timeSigChange.Insert(0, new(0, TimeSignature.Default));
    
    var timeQueue = new Queue<TimeSignatureEvent>(timeSigChange);
    var clickInfos = new List<ClickInfo>();
    var currentTimeSignature = timeQueue.Dequeue().TimeSignature;
    var oneBeatMusicalLength = GetOneBeatMusicalLength(currentTimeSignature);
    var currentMidiTime = 0L;
    
    var oneBeatMetricLength = LengthConverter.ConvertTo<MetricTimeSpan>(oneBeatMusicalLength, 0, sourceTempoMap);
    for (var i = 0; i < currentTimeSignature.Numerator; i++)
    {
        clickInfos.Add(new(i * oneBeatMetricLength.TotalMicroseconds, i == 0 ? ClickType.PreparePrimary : ClickType.PrepareSecondary));
    }

    var offsetMicroseconds = oneBeatMetricLength.TotalMicroseconds * currentTimeSignature.Numerator;
    currentMidiTime = 0;
    var remainingBeats = currentTimeSignature.Numerator;
    while (currentMidiTime < maxMidiClicks)
    {
        clickInfos.Add(new(TimeConverter.ConvertTo<MetricTimeSpan>(currentMidiTime, sourceTempoMap).TotalMicroseconds + offsetMicroseconds,
            remainingBeats == currentTimeSignature.Numerator
                ? ClickType.Primary
                : ClickType.Secondary));

        remainingBeats--;

        if (remainingBeats <= 0) remainingBeats = currentTimeSignature.Numerator;

        currentMidiTime += LengthConverter.ConvertFrom(
            oneBeatMusicalLength,
            currentMidiTime,
            sourceTempoMap
        );
        
        if (timeQueue.TryPeek(out var newTime) && newTime.MidiTime <= currentMidiTime)
        {
            currentTimeSignature = timeQueue.Dequeue().TimeSignature;
            oneBeatMusicalLength = GetOneBeatMusicalLength(currentTimeSignature);
            remainingBeats = currentTimeSignature.Numerator;
        }
    }
    
    var lastClickMicrosecond = TimeConverter.ConvertTo<MetricTimeSpan>(currentMidiTime, sourceTempoMap).TotalMicroseconds + offsetMicroseconds;
    clickInfos.Add(new(lastClickMicrosecond, ClickType.Final));

    return clickInfos;
}

static MusicalTimeSpan GetOneBeatMusicalLength(TimeSignature timeSignature)
{
    return new(1, timeSignature.Denominator);
}

static void CreateAudioTrack(ReadOnlySpan<ClickInfo> clickInfo, string dstPath, string primaryClickPath, string secondaryClickPath)
{
    var sampleSequence = new List<ISampleProvider>();
    var currentMicrosecond = 0L;
    foreach (var click in clickInfo)
    {
        AudioFileReader clickSample = click.Type switch
        {
            ClickType.Primary => new(primaryClickPath),
            ClickType.Secondary => new(secondaryClickPath),
            ClickType.PreparePrimary => new(primaryClickPath),
            ClickType.PrepareSecondary => new(secondaryClickPath),
            ClickType.Final => new(primaryClickPath),
            _ => throw new UnreachableException()
        };

        if (sampleSequence.Count == 0)
        {
            sampleSequence.Add(clickSample);
        }
        else
        {
            var lastSample = sampleSequence[^1];
            TimeSpan lastSampleDuration;
            switch (lastSample)
            {
                case AudioFileReader fileSample:
                    lastSampleDuration = fileSample.TotalTime;
                    break;
                case OffsetSampleProvider offsetSample:
                    lastSampleDuration = ((AudioFileReader)offsetSample.UnsafeAccessInternalSampleProvider()).TotalTime;
                    break;
                default:
                    throw new UnreachableException();
            }
            
            var offsetSampleProvider = new OffsetSampleProvider(clickSample);
            var delayAmount = TimeSpan.FromMicroseconds(click.Microsecond - currentMicrosecond) - lastSampleDuration;
            offsetSampleProvider.DelayBy = delayAmount;
            sampleSequence.Add(offsetSampleProvider);
        }

        currentMicrosecond = click.Microsecond;
    }

    var concatenatingSampleProvider = new ConcatenatingSampleProvider(sampleSequence);
    // var output = new DirectSoundOut(1024);
    // output.Init(concatenatingSampleProvider);
    // output.Play();
    // while (output.PlaybackState == PlaybackState.Playing)
    // {
    //     Thread.Sleep(2000);
    // }

    WaveFileWriter.CreateWaveFile16(dstPath, concatenatingSampleProvider);
}

public enum ClickType
{
    PreparePrimary,
    PrepareSecondary,
    Primary,
    Secondary,
    Final,
}

internal readonly record struct ClickInfo(
    long Microsecond,
    ClickType Type
);

internal record struct TimeSignatureEvent(long MidiTime, TimeSignature TimeSignature);

internal record struct TempoEvent(long MidiTime, Tempo Tempo);

public static class Accessor
{
    public static ISampleProvider UnsafeAccessInternalSampleProvider(this OffsetSampleProvider provider) =>
        AccessorImpl1(provider);
    
    public static void UnsafeChangeTime(this MetricTimeSpan metricTimeSpan, long totalMicroseconds) =>
        AccessorImpl2(metricTimeSpan) = new(totalMicroseconds * 10);
    
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "sourceProvider")]
    private static extern ref ISampleProvider AccessorImpl1(OffsetSampleProvider provider);
    
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_timeSpan")]
    private static extern ref TimeSpan AccessorImpl2(MetricTimeSpan time);
}
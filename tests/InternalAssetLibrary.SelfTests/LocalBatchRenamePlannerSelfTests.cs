using InternalAssetLibrary.Client.Core.LocalAssets;

internal static class LocalBatchRenamePlannerSelfTests
{
    public static void NamesFollowVisibleOrderAndPreserveExtensions()
    {
        var plan = LocalBatchRenamePlanner.Create(
            ["Zulu.MP3", "alpha.wav", "middle.FLAC"],
            "团队-",
            "-精选");

        Equal(2, plan.SequenceDigits);
        SequenceEqual(
            ["团队-01-精选.MP3", "团队-02-精选.wav", "团队-03-精选.FLAC"],
            plan.Items.Select(item => item.NewFileName));
        SequenceEqual(
            ["Zulu.MP3", "alpha.wav", "middle.FLAC"],
            plan.Items.Select(item => item.OriginalFileName));
        SequenceEqual([1, 2, 3], plan.Items.Select(item => item.Sequence));

        var numberOnly = LocalBatchRenamePlanner.Create(["clip.mov"], null, null);
        Equal("01.mov", numberOnly.Items.Single().NewFileName);
    }

    public static void SequenceWidthExpandsBeyondNinetyNine()
    {
        var originals = Enumerable.Range(1, 100)
            .Select(index => $"original-{index}.mp4")
            .ToArray();
        var plan = LocalBatchRenamePlanner.Create(originals, "V", null);

        Equal(3, plan.SequenceDigits);
        Equal("V001.mp4", plan.Items[0].NewFileName);
        Equal("V099.mp4", plan.Items[98].NewFileName);
        Equal("V100.mp4", plan.Items[99].NewFileName);
    }

    public static void EmptySelectionsAndInvalidNamePartsAreRejected()
    {
        Throws<ArgumentException>(() => LocalBatchRenamePlanner.Create([], null, null));
        Throws<ArgumentException>(() => LocalBatchRenamePlanner.Create([""], null, null));
        Throws<ArgumentException>(() => LocalBatchRenamePlanner.Create(["folder/clip.mp4"], null, null));
        Throws<ArgumentException>(() => LocalBatchRenamePlanner.Create(["clip.mp4"], "bad?", null));
        Throws<ArgumentException>(() => LocalBatchRenamePlanner.Create(["clip.mp4"], null, "bad\n"));
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', but was '{actual}'.");
        }
    }

    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException("Sequences are not equal.");
        }
    }

    private static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name} to be thrown.");
    }
}

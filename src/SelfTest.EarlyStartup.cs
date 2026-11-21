namespace ClearStart;

// Early startup staying on: what ClearStart does when its shell entry has gone, on made-up registry values, and the
// record that tells an entry the user turned off from one the loader removed, on a file of the self-test's own.
// Neither the registry nor the real record is read or changed.
internal static partial class SelfTest
{
    private static object CheckEarlyStartupRecord(string directory, List<string> errors)
    {
        const string mine = "\"C:\\Programs\\ClearStart\\ClearStart.Loader.exe\" --shell";
        const string other = "\"D:\\Dev\\ClearStart\\ClearStart.Loader.exe\" --shell";
        const string foreign = "C:\\Tools\\othershell.exe";

        // (the shell entry, on record, as the shell starts, started by the loader) and what that calls for.
        var cases = new (string Name, string? Shell, bool Recorded, bool Starting, bool UnderLoader, EarlyStartup.Step Want)[]
        {
            ("starting with it on and no record", mine, false, true, false, EarlyStartup.Step.Remember),
            ("starting with it on, in other letter case", mine.ToUpperInvariant(), false, true, false, EarlyStartup.Step.Remember),
            ("starting with it on and on record", mine, true, true, false, EarlyStartup.Step.None),
            ("starting with it off and no record", null, false, true, false, EarlyStartup.Step.None),
            ("starting with the entry gone and a record", null, true, true, false, EarlyStartup.Step.Forget),
            ("starting with another copy's entry and a record", other, true, true, false, EarlyStartup.Step.Forget),
            ("starting under the loader with it on", mine, false, true, true, EarlyStartup.Step.Remember),
            ("running, the entry gone and a record", null, true, false, false, EarlyStartup.Step.PutBack),
            ("running, the entry gone and no record", null, false, false, false, EarlyStartup.Step.None),
            ("running with it on", mine, true, false, false, EarlyStartup.Step.None),
            ("running, another program as the shell", foreign, true, false, false, EarlyStartup.Step.None),
            ("running, another copy's entry", other, true, false, false, EarlyStartup.Step.None),
            ("running under the loader, the entry gone", null, true, false, true, EarlyStartup.Step.None),
        };
        int right = 0;
        foreach (var c in cases)
        {
            var got = EarlyStartup.Next(c.Shell, c.Recorded, mine, c.Starting, c.UnderLoader);
            if (got == c.Want) right++;
            else errors.Add($"Early startup staying on: {c.Name} should give {c.Want}, not {got}");
        }

        // The record itself.
        string file = Path.Combine(directory, "early-startup-on-selftest", "record.txt");
        bool recordWorks = false;
        try
        {
            if (File.Exists(file)) File.Delete(file);
            bool none = !EarlyStartup.Recorded(file, mine);
            EarlyStartup.Remember(file, mine);
            bool there = EarlyStartup.Recorded(file, mine) && EarlyStartup.Recorded(file, mine.ToUpperInvariant());
            bool notOthers = !EarlyStartup.Recorded(file, other);
            // Another copy turning its own early startup off leaves this copy's record alone.
            EarlyStartup.Forget(file, other);
            bool kept = EarlyStartup.Recorded(file, mine);
            EarlyStartup.Forget(file, mine);
            bool gone = !File.Exists(file);
            // Forgetting what is not there is not an error.
            EarlyStartup.Forget(file, mine);

            if (!none) errors.Add("Early startup staying on: a record that is not there was read as there");
            if (!there) errors.Add("Early startup staying on: the record was not read back");
            if (!notOthers) errors.Add("Early startup staying on: one copy's record was read as another's");
            if (!kept) errors.Add("Early startup staying on: another copy took this copy's record away");
            if (!gone) errors.Add("Early startup staying on: the record stayed after it was forgotten");
            recordWorks = none && there && notOthers && kept && gone;
        }
        finally
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
                Directory.Delete(Path.GetDirectoryName(file)!);
            }
            catch (Exception ex)
            {
                Log.Warn("Could not remove the self-test's early-startup record: " + ex.Message);
            }
        }

        return new { casesTried = cases.Length, casesRight = right, recordWorks };
    }
}

namespace CddaCompanion.Features;

/// <summary>Progress reporting for a long-running job.</summary>
public sealed class JobContext
{
    readonly Jobs _owner;
    internal JobContext(Jobs owner) { _owner = owner; }
    public void Progress(string text, long done = 0, long total = 0)
    {
        lock (_owner.Lock) { _owner.Text = text; _owner.Done = done; _owner.Total = total; }
    }
}

/// <summary>Runs one heavy job at a time (snapshot refresh, backup, restore) so they never overlap.</summary>
public sealed class Jobs
{
    internal readonly object Lock = new();
    public string Name, Text, Error, Result;
    public long Done, Total;
    public bool Running;
    public DateTime FinishedUtc;

    public bool TryStart(string name, Func<JobContext, string> work)
    {
        lock (Lock)
        {
            if (Running) return false;
            Running = true; Name = name; Text = "starting"; Error = null; Result = null; Done = Total = 0;
        }
        var ctx = new JobContext(this);
        var t = new Thread(() =>
        {
            string result = null, error = null;
            try { result = work(ctx); }
            catch (Exception ex) { error = ex.Message; Console.Error.WriteLine($"[{name}] {ex}"); }
            lock (Lock) { Running = false; Result = result; Error = error; FinishedUtc = DateTime.UtcNow; Text = null; }
        }) { IsBackground = true, Name = "job-" + name };
        t.Start();
        return true;
    }
}

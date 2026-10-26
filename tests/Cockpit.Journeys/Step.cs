namespace Cockpit.Journeys;

// A journey that walks several steps names the one that broke: its failure reads "<step>: <what failed>".
public static class Step
{
    public static async Task Run(string name, Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (Exception failure) when (failure is not StepFailedException)
        {
            throw new StepFailedException(name, failure);
        }
    }
}

public sealed class StepFailedException(string step, Exception failure) : Exception($"{step}: {failure.Message}", failure);

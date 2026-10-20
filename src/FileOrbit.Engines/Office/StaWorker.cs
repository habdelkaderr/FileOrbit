namespace FileOrbit.Engines.Office;

/// <summary>Runs COM automation on a dedicated single-threaded-apartment thread.</summary>
internal static class StaWorker
{
    public static Task<T> Run<T>(Func<T> work, string name)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(work());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally
            {
                // Release runtime-callable wrappers created through 'dynamic' on this thread.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        })
        {
            IsBackground = true,
            Name = name,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    public static Task Run(Action work, string name) => Run(() =>
    {
        work();
        return true;
    }, name);
}

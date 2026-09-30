using System.Collections.Specialized;
using System.ComponentModel;

namespace Cockpit.Journeys;

// The journeys' only way to wait: until a condition holds, checked now and again each time its subject announces a
// change. `Ceiling` is a guard against hanging and never passes on a green run.
public static class Until
{
    public static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    public static Task Holds(INotifyPropertyChanged subject, Func<bool> condition) =>
        _Holds(condition, changed =>
        {
            PropertyChangedEventHandler handler = (_, _) => changed();
            subject.PropertyChanged += handler;
            return () => subject.PropertyChanged -= handler;
        });

    public static Task CollectionHolds(INotifyCollectionChanged subject, Func<bool> condition) =>
        _Holds(condition, changed =>
        {
            NotifyCollectionChangedEventHandler handler = (_, _) => changed();
            subject.CollectionChanged += handler;
            return () => subject.CollectionChanged -= handler;
        });

    private static Task _Holds(Func<bool> condition, Func<Action, Action> subscribe)
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action unsubscribe = () => { };
        void Check()
        {
            if (condition())
            {
                unsubscribe();
                held.TrySetResult();
            }
        }

        unsubscribe = subscribe(Check);
        Check();
        return held.Task.WaitAsync(Ceiling);
    }
}

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;

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

    public static Task LayoutHolds(Control subject, Func<bool> condition) =>
        _Holds(condition, changed =>
        {
            EventHandler handler = (_, _) => changed();
            subject.LayoutUpdated += handler;
            return () => subject.LayoutUpdated -= handler;
        });

    public static Task CollectionHolds(INotifyCollectionChanged subject, Func<bool> condition) =>
        _Holds(condition, changed =>
        {
            NotifyCollectionChangedEventHandler handler = (_, _) => changed();
            subject.CollectionChanged += handler;
            return () => subject.CollectionChanged -= handler;
        });

    // As CollectionHolds, and also when an item already in it changes: a row added empty and filled in afterwards, as a
    // transcript row arrives in two upserts, changes no collection the second time.
    public static Task ItemsHold<T>(ObservableCollection<T> items, Func<bool> condition)
        where T : INotifyPropertyChanged =>
        _Holds(condition, changed =>
        {
            PropertyChangedEventHandler itemChanged = (_, _) => changed();
            var watched = new List<T>();
            void Watch()
            {
                foreach (var item in items.Except(watched).ToList())
                {
                    item.PropertyChanged += itemChanged;
                    watched.Add(item);
                }
            }

            NotifyCollectionChangedEventHandler collectionChanged = (_, _) =>
            {
                Watch();
                changed();
            };
            Watch();
            items.CollectionChanged += collectionChanged;
            return () =>
            {
                items.CollectionChanged -= collectionChanged;
                foreach (var item in watched)
                {
                    item.PropertyChanged -= itemChanged;
                }
            };
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

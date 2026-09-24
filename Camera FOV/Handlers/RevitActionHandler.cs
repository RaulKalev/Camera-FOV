using Autodesk.Revit.UI;
using Camera_FOV.UI;
using System;
using System.Collections.Generic;

namespace Camera_FOV.Handlers
{
    /// <summary>
    /// Runs actions from the plugin's secondary windows (such as the camera type configurator) on the
    /// Revit thread, in the order they were asked for. Each action opens its own transaction.
    /// </summary>
    public sealed class RevitActionHandler : IExternalEventHandler
    {
        private readonly object _lock = new object();
        private readonly Queue<(string Description, Action<UIApplication> Action)> _pending = new Queue<(string, Action<UIApplication>)>();
        private readonly ExternalEvent _externalEvent;

        // Must be constructed in a Revit API context (e.g. while an external command runs).
        public RevitActionHandler()
        {
            _externalEvent = ExternalEvent.Create(this);
        }

        /// <summary>Queues the action; returns false with the reason when Revit won't run it.</summary>
        public bool Enqueue(string description, Action<UIApplication> action, out string error)
        {
            lock (_lock) _pending.Enqueue((description, action));

            ExternalEventRequest result = _externalEvent.Raise();
            if (result == ExternalEventRequest.Accepted || result == ExternalEventRequest.Pending)
            {
                error = null;
                return true;
            }

            lock (_lock) _pending.Clear();
            error = $"Revit did not accept the request ({result}). Nothing was changed in the model; please try again.";
            return false;
        }

        public void Execute(UIApplication app)
        {
            while (true)
            {
                (string Description, Action<UIApplication> Action) next;
                lock (_lock)
                {
                    if (_pending.Count == 0) return;
                    next = _pending.Dequeue();
                }

                try
                {
                    next.Action(app);
                }
                catch (Exception ex)
                {
                    MessageDialog.ShowError($"Couldn’t {next.Description}", "Nothing was changed in the model.", ex);
                }
            }
        }

        public string GetName() => "Camera FOV actions";
    }
}

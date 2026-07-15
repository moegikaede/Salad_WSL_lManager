using System;
using System.Collections.Generic;

internal static partial class Program
{
    private struct AppStateSnapshot
    {
        public readonly string Salad;
        public readonly string Bowl;
        public readonly string Wsl;
        public readonly string State;
        public readonly string Workload;
        public readonly string Pull;
        public readonly string Runtime;
        public readonly string PastAverage;
        public readonly string EstimatedPerHour;
        public readonly string Last24Hours;
        public readonly string Balance;

        public AppStateSnapshot(
            string salad,
            string bowl,
            string wsl,
            string state,
            string workload,
            string pull,
            string runtime,
            string pastAverage,
            string estimatedPerHour,
            string last24Hours,
            string balance)
        {
            // Normalize presentation only; controllers continue using their original machine-readable states.
            Salad = NormalizeOperationalState(salad);
            Bowl = NormalizeOperationalState(bowl);
            Wsl = NormalizeOperationalState(wsl);
            State = ValueOrUnknown(state);
            Workload = ValueOrUnknown(workload);
            Pull = NormalizePullValue(pull);
            Runtime = ValueOrUnknown(runtime);
            PastAverage = ValueOrUnknown(pastAverage);
            EstimatedPerHour = NormalizeEstimatedPerHour(estimatedPerHour);
            Last24Hours = ValueOrUnknown(last24Hours);
            Balance = ValueOrUnknown(balance);
        }

        private static string NormalizeOperationalState(string value)
        {
            var state = ValueOrUnknown(value);
            if (string.Equals(state, "RUNNING", StringComparison.OrdinalIgnoreCase))
            {
                return "Running";
            }

            if (string.Equals(state, "STOPPED", StringComparison.OrdinalIgnoreCase))
            {
                return "Stopped";
            }

            if (string.Equals(state, "NOT_FOUND", StringComparison.OrdinalIgnoreCase))
            {
                return "Not found";
            }

            if (string.Equals(state, "START_PENDING", StringComparison.OrdinalIgnoreCase))
            {
                return "Starting";
            }

            if (string.Equals(state, "STOP_PENDING", StringComparison.OrdinalIgnoreCase))
            {
                return "Stopping";
            }

            return state;
        }

        public AppStateSnapshot WithLocalLogState(
            string state,
            string workload,
            string pull,
            string runtime,
            string pastAverage)
        {
            return new AppStateSnapshot(
                Salad,
                Bowl,
                Wsl,
                state,
                workload,
                pull,
                runtime,
                pastAverage,
                EstimatedPerHour,
                Last24Hours,
                Balance);
        }

        public string ToStatusString()
        {
            return string.Join(" | ", ToStatusLines());
        }

        public string[] ToStatusLines()
        {
            return new[]
            {
                "Salad: " + Salad,
                "Bowl: " + Bowl,
                "WSL: " + Wsl,
                "State: " + State,
                "Workload: " + Workload,
                "Pull: " + Pull,
                "Runtime: " + Runtime,
                "Past avg: " + PastAverage,
                "Est/hr: " + EstimatedPerHour,
                "Last24h: " + Last24Hours,
                "Balance: " + Balance
            };
        }

        public Dictionary<string, string> ToStatusParts()
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Salad", Salad },
                { "Bowl", Bowl },
                { "WSL", Wsl },
                { "State", State },
                { "Workload", Workload },
                { "Pull", Pull },
                { "Runtime", Runtime },
                { "Past avg", PastAverage },
                { "Est/hr", EstimatedPerHour },
                { "Last24h", Last24Hours },
                { "Balance", Balance }
            };
        }

        private static string ValueOrUnknown(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "?" : value.Trim();
        }

        private static string NormalizePullValue(string value)
        {
            var text = ValueOrUnknown(value);
            return text.StartsWith("Pull:", StringComparison.OrdinalIgnoreCase)
                ? ValueOrUnknown(text.Substring("Pull:".Length))
                : text;
        }

        private static string NormalizeEstimatedPerHour(string value)
        {
            var text = ValueOrUnknown(value);
            return text.StartsWith("Est/hr", StringComparison.OrdinalIgnoreCase)
                ? ValueOrUnknown(text.Substring("Est/hr".Length).TrimStart(':').Trim())
                : text;
        }
    }
}

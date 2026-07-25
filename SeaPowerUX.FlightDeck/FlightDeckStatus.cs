using System.Collections.Generic;
using System.Text;
using SeaPower;
using UnityEngine;

namespace SeaPowerUX.FlightDeck
{
    /// <summary>
    /// A snapshot of what a ship's flight deck is doing, summarised from its live task list.
    ///
    /// Everything here is derived from <c>FlightDeck.FlightDeckTasks</c>, which is the same source
    /// the game's own "Flight Operations" table binds to. The task subclass IS the state: a
    /// PendingLaunchTask is an aircraft being readied, a LaunchTask is one going off the deck, and
    /// so on. Nothing needs to be inferred or timed.
    /// </summary>
    internal struct FlightDeckStatus
    {
        public int Readying;    // PendingLaunchTask still in ready-up — being prepped
        public int ReadyToLaunch; // PendingLaunchTask past ready-up — waiting on the player
        public int Launching;   // LaunchTask
        public int Recovering;  // RecoveryTask
        public int Returning;   // ReturnTask — RTB
        public int CoolingDown; // CoolDownTask
        public int Holding;     // aircraft stacked waiting to land

        public bool Idle =>
            Readying + ReadyToLaunch + Launching + Recovering + Returning + CoolingDown + Holding == 0;

        /// <summary>
        /// The single most operationally significant thing happening, for the summary column.
        /// Ordered by what a player most needs to notice: aircraft actually moving on or off the
        /// deck outranks aircraft merely being prepared.
        /// </summary>
        public string PrimaryLabel
        {
            get
            {
                if (Launching > 0) return "Launching";
                if (Recovering > 0) return "Recovering";
                if (Holding > 0) return "Inbound";
                // Above RTB: aircraft sitting ready are waiting on a player decision, which is
                // more actionable than traffic that's simply on its way home.
                if (ReadyToLaunch > 0) return "Ready";
                if (Returning > 0) return "RTB";
                if (Readying > 0) return "Readying";
                if (CoolingDown > 0) return "Cooldown";
                return "—";
            }
        }

        public static FlightDeckStatus Summarize(SeaPower.FlightDeck deck)
        {
            FlightDeckStatus status = default;
            if (deck == null) return status;

            try
            {
                foreach (FlightDeckTask task in deck.FlightDeckTasks)
                {
                    if (task == null) continue;

                    if (task is LaunchTask) status.Launching++;
                    else if (task is RecoveryTask) status.Recovering++;
                    else if (task is ReturnTask) status.Returning++;
                    else if (task is CoolDownTask) status.CoolingDown++;
                    else if (task is PendingLaunchTask pending)
                    {
                        // Count AIRCRAFT, not tasks: one PendingLaunchTask can cover a whole
                        // flight, which is why a "2 ready to launch" row previously showed as 1.
                        int aircraft = Mathf.Max(1, pending.LaunchCount);

                        // Same task type either side of ready-up; the state machine is what says
                        // which. HandleReadyUpTask is the prep phase — once it exits, the task is
                        // sitting at "Ready to Launch" waiting on the player.
                        if (IsStillPreparing(pending)) status.Readying += aircraft;
                        else status.ReadyToLaunch += aircraft;
                    }
                }

                if (deck._holdingQueue != null) status.Holding = deck._holdingQueue.Count;
            }
            catch
            {
                // A ship mid-teardown can throw here; an empty summary is the right answer.
            }

            return status;
        }

        /// <summary>
        /// True while a pending launch is still being prepared (ready-up, or blocked waiting on
        /// ground crew or deck space). False once it's queued and ready to go.
        /// </summary>
        private static bool IsStillPreparing(PendingLaunchTask pending)
        {
            try
            {
                object state = pending._stateMachine?.CurrentState;
                if (state == null) return true; // not started yet — treat as preparing

                return state is HandleReadyUpTask
                    || state is HandleAwaitGroundCrewTask
                    || state is HandleAwaitDeckSpaceTask;
            }
            catch
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Renders a status as coloured glyphs for the at-a-glance column.
    ///
    /// One Text component with rich text rather than several cells: Unity's Text supports
    /// &lt;color&gt; tags, so a whole multi-state summary fits in a single graphic. That keeps the
    /// row cheap — extra cells would mean extra layout children on every row.
    /// </summary>
    internal static class FlightDeckGlyphs
    {
        // Geometric shapes and arrows only — these exist in Liberation Sans / Arial, which is what
        // UguiWindow.GetUiFont() falls back through. Anything more exotic risks tofu boxes.
        private const string LaunchGlyph = "▲";   // ▲ up: leaving the deck
        private const string RecoverGlyph = "▼";  // ▼ down: coming aboard
        private const string HoldGlyph = "●";     // ● stacked, waiting
        private const string ReturnGlyph = "←";   // ← heading home
        private const string PrepGlyph = "◇";     // ◇ hollow: being prepped
        private const string ReadyGlyph = "◆";    // ◆ filled: prepped and awaiting the launch order
        private const string CoolGlyph = "■";     // ■ deck cooling

        private const string LaunchColor = "#7ac943";
        private const string RecoverColor = "#4a9fd8";
        private const string HoldColor = "#d8c34a";
        private const string ReturnColor = "#4ad8c3";
        private const string PrepColor = "#d89a4a";
        private const string ReadyColor = "#e8c23a";
        private const string CoolColor = "#949aa1";

        public static string Render(FlightDeckStatus status)
        {
            if (status.Idle) return string.Empty;

            var sb = new StringBuilder(64);
            Append(sb, LaunchGlyph, LaunchColor, status.Launching);
            Append(sb, RecoverGlyph, RecoverColor, status.Recovering);
            Append(sb, HoldGlyph, HoldColor, status.Holding);
            Append(sb, ReturnGlyph, ReturnColor, status.Returning);
            Append(sb, ReadyGlyph, ReadyColor, status.ReadyToLaunch);
            Append(sb, PrepGlyph, PrepColor, status.Readying);
            Append(sb, CoolGlyph, CoolColor, status.CoolingDown);
            return sb.ToString();
        }

        /// <summary>Legend text, so the glyphs are discoverable rather than guesswork.</summary>
        public static string Legend()
        {
            var sb = new StringBuilder(160);
            Append(sb, LaunchGlyph, LaunchColor, 1, showCount: false);
            sb.Append("launch  ");
            Append(sb, RecoverGlyph, RecoverColor, 1, showCount: false);
            sb.Append("recover  ");
            Append(sb, HoldGlyph, HoldColor, 1, showCount: false);
            sb.Append("holding  ");
            Append(sb, ReturnGlyph, ReturnColor, 1, showCount: false);
            sb.Append("RTB  ");
            Append(sb, ReadyGlyph, ReadyColor, 1, showCount: false);
            sb.Append("ready  ");
            Append(sb, PrepGlyph, PrepColor, 1, showCount: false);
            sb.Append("readying  ");
            Append(sb, CoolGlyph, CoolColor, 1, showCount: false);
            sb.Append("cooldown");
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, string glyph, string color, int count, bool showCount = true)
        {
            if (count <= 0) return;
            if (sb.Length > 0) sb.Append(' ');

            sb.Append("<color=").Append(color).Append('>').Append(glyph);
            if (showCount) sb.Append(count);
            sb.Append("</color>");
        }
    }
}

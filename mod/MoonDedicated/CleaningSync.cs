using System;
using System.Runtime.InteropServices;

namespace MoonDedicated
{
    internal static class CleaningSync
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Repair(IntPtr world, IntPtr chunk, int key, IntPtr bytes, IntPtr mi);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Polish(IntPtr world, IntPtr chunk, IntPtr bounds, float radius, IntPtr mi);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate byte Joins(IntPtr world, IntPtr run, IntPtr stroke, IntPtr mi);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Stroke(IntPtr crew, IntPtr stroke, IntPtr mi);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate byte QueueJoins(IntPtr queued, IntPtr from, IntPtr right, float width, float depth, IntPtr mi);
        static Repair originalRepair;
        static Polish originalPolish;
        static Joins originalJoins;
        static Stroke originalStroke;
        static QueueJoins originalQueueJoins;
        static D_Inst0 flushStroke;
        [ThreadStatic] static int receivingPage;
        [ThreadStatic] static bool completingStrokes;

        internal static bool RepairFailed { get; private set; }
        internal static bool SuppressRepairPolish => receivingPage > 0;
        internal static void ResetRepairFailure() => RepairFailed = false;
        public static void Install()
        {
            // A CPU-only host regards every player as distant. Joining a curved
            // series into one endpoint-to-endpoint stroke adds a chord that was
            // never brushed. Keep each original segment and its tool parameters.
            originalJoins = Hooks.Method(Game.KWorld, "Joins", 2, (Joins)((world, run, stroke, mi) =>
                Cfg.Role == Role.Server || PlayerClient.IsDedicated ? (byte)0 : originalJoins(world, run, stroke, mi)));
            originalQueueJoins = Hooks.Method(Game.KWorld, "JoinsStraight", 5, (QueueJoins)((queued, from, right, width, depth, mi) =>
                Cfg.Role == Role.Server || PlayerClient.IsDedicated ? (byte)0 : originalQueueJoins(queued, from, right, width, depth, mi)));
            flushStroke = Marshal.GetDelegateForFunctionPointer<D_Inst0>(Il2.Code(Il2.Method(Game.KCrew, "FlushStroke", 0)));
            originalStroke = Hooks.Method(Game.KCrew, "OnStroke", 1, (Stroke)((crew, stroke, mi) => {
                originalStroke(crew, stroke, mi);
                NativeSyncDiagnostics.RecordStroke(stroke);
                // Do not let the sender collapse several curved segments before
                // the dedicated host has a chance to receive their geometry.
                if (Cfg.Role == Role.Server || PlayerClient.IsDedicated) flushStroke(crew, IntPtr.Zero);
            }));
            originalRepair = Hooks.Method(Game.KWorld, "CleanDownTo", 3, (Repair)((world, chunk, key, bytes, mi) => {
                bool dedicated = Cfg.Role == Role.Server || PlayerClient.IsDedicated;
                // A page is cumulative state after those strokes, not another
                // dose of removal. Finish older queued/held strokes first so
                // they cannot later subtract from the already repaired page.
                if (dedicated && !completingStrokes && receivingPage == 0)
                {
                    completingStrokes = true;
                    try
                    {
                        if (!Il2.TryInvoke(Il2.Method(Game.KWorld, "CompletePendingToolStrokes", 0), world, out _))
                        {
                            RepairFailed = true;
                            Log.Error("Dust synchronization failed: pending strokes could not complete. Reconnect before continuing: " + Il2.LastError);
                            return 0;
                        }
                    }
                    finally { completingStrokes = false; }
                }
                if (dedicated) receivingPage++;
                try { return originalRepair(world, chunk, key, bytes, mi); }
                finally { if (dedicated) receivingPage--; }
            }));
            originalPolish = Hooks.Method(Game.KWorld, "PolishDust", 3, (Polish)((world, chunk, bounds, radius, mi) =>
                SuppressRepairPolish ? 0 : originalPolish(world, chunk, bounds, radius, mi)));
            // CleanDownTo receives raw removal samples, without the originating
            // tool's edge width. Its normal FinishCleaning pass substitutes a
            // much larger polish radius (.35 versus ~.03 for the hand brush),
            // filling loops and gaps. Preserve the native merge, dirty tracking,
            // collision refresh and upload; suppress only that extra removal.
            // Actual tool strokes still use the game's normal polishing path.
            Log.Info("Dedicated cleaning preserves individual tool segments and received dust page shapes");
        }
    }
}

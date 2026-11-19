using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SparkBar;

/// <summary>
/// Ultra-fast, zero-marshaling COM vtable wrapper for Windows UI Automation (IUIAutomation)
/// plus 3ms fast clipboard probe fallback and editable-focus detection.
/// </summary>
internal static unsafe class UiaHelper
{
    private static readonly Guid CLSID_CUIAutomation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");
    private static readonly Guid IID_IUIAutomation   = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");

    [ThreadStatic] private static IntPtr _tlsUia;
    [ThreadStatic] private static IntPtr _tlsWalker;
    [ThreadStatic] private static bool _tlsInitTried;

    private static readonly uint CfExcludeMonitor = Native.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint CfCanIncludeHistory = Native.RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly uint CfCanUploadCloud = Native.RegisterClipboardFormat("CanUploadToCloudClipboard");

    private static readonly HashSet<int> ReadOnlyControlTypes = new()
    {
        50005, // Hyperlink
        50006, // Image
        50007, // ListItem
        50020, // Text
        50024, // ToolTip
        50029  // DataItem
    };

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct VARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public int lVal;
        [FieldOffset(8)] public short boolVal;
        [FieldOffset(8)] public IntPtr ptrVal;
    }

    private static IntPtr GetUia()
    {
        if (_tlsUia != IntPtr.Zero) return _tlsUia;
        if (_tlsInitTried) return IntPtr.Zero;
        _tlsInitTried = true;
        try
        {
            Native.CoInitializeEx(IntPtr.Zero, 0 /*COINIT_MULTITHREADED*/);
            if (Native.CoCreateInstance(in CLSID_CUIAutomation, IntPtr.Zero, 1 /*CLSCTX_INPROC_SERVER*/, in IID_IUIAutomation, out IntPtr p) == 0)
                _tlsUia = p;
        }
        catch { }
        return _tlsUia;
    }

    private static IntPtr GetControlWalker()
    {
        if (_tlsWalker != IntPtr.Zero) return _tlsWalker;
        IntPtr uia = GetUia();
        if (uia == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            void** vtbl = *(void***)uia;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vtbl[14];
            IntPtr w = IntPtr.Zero;
            if (fn(uia, &w) == 0 && w != IntPtr.Zero)
                _tlsWalker = w;
        }
        catch { }
        return _tlsWalker;
    }

    private static IntPtr GetElementFromPoint(int x, int y)
    {
        IntPtr uia = GetUia();
        if (uia == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            void** vtbl = *(void***)uia;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Native.POINT, IntPtr*, int>)vtbl[7];
            IntPtr el = IntPtr.Zero;
            return fn(uia, new Native.POINT(x, y), &el) == 0 ? el : IntPtr.Zero;
        }
        catch { return IntPtr.Zero; }
    }

    private static IntPtr GetFocusedElement()
    {
        IntPtr uia = GetUia();
        if (uia == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            void** vtbl = *(void***)uia;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vtbl[8];
            IntPtr el = IntPtr.Zero;
            return fn(uia, &el) == 0 ? el : IntPtr.Zero;
        }
        catch { return IntPtr.Zero; }
    }

    private static object GetProperty(IntPtr el, int propId)
    {
        if (el == IntPtr.Zero) return null;
        VARIANT v = default;
        try
        {
            void** vtbl = *(void***)el;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, VARIANT*, int>)vtbl[10];
            if (fn(el, propId, &v) == 0)
            {
                if (v.vt == 11) return v.boolVal != 0 ? 1 : 0;
                if (v.vt == 3) return v.lVal;
            }
        }
        catch { }
        finally { Native.VariantClear(&v); }
        return null;
    }

    private static bool TryGetNonEmptySelection(IntPtr el, out IntPtr rangeArr, out bool hasTextPattern)
    {
        rangeArr = IntPtr.Zero;
        hasTextPattern = false;
        if (el == IntPtr.Zero) return false;

        if (GetProperty(el, 30019 /*IsPassword*/) is int isPwd && isPwd == 1)
        {
            hasTextPattern = true;
            return false;
        }

        void** elVtbl = *(void***)el;
        var getPat = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)elVtbl[16];
        foreach (int patternId in new[] { 10014 /*UIA_TextPatternId*/, 10024 /*UIA_TextPattern2Id*/ })
        {
            IntPtr pat = IntPtr.Zero;
            if (getPat(el, patternId, &pat) == 0 && pat != IntPtr.Zero)
            {
                hasTextPattern = true;
                try
                {
                    void** patVtbl = *(void***)pat;
                    var getSel = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)patVtbl[5];
                    IntPtr arr = IntPtr.Zero;
                    if (getSel(pat, &arr) == 0 && arr != IntPtr.Zero)
                    {
                        void** arrVtbl = *(void***)arr;
                        var getLen = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)arrVtbl[3];
                        int count = 0;
                        if (getLen(arr, &count) == 0 && count > 0)
                        {
                            rangeArr = arr;
                            return true;
                        }
                        Marshal.Release(arr);
                    }
                }
                finally { Marshal.Release(pat); }
            }
        }
        return false;
    }

    private static IntPtr TryGetSelectionFromElementOrAncestors(IntPtr startEl, out bool supported)
    {
        supported = false;
        if (startEl == IntPtr.Zero) return IntPtr.Zero;

        IntPtr walker = GetControlWalker();
        IntPtr cur = startEl;
        Marshal.AddRef(cur);

        for (int depth = 0; depth < 10 && cur != IntPtr.Zero; depth++)
        {
            if (TryGetNonEmptySelection(cur, out IntPtr arr, out bool hasPat))
            {
                supported = true;
                Marshal.Release(cur);
                return arr;
            }
            if (hasPat) supported = true;

            if (walker == IntPtr.Zero) break;
            IntPtr next = IntPtr.Zero;
            try
            {
                void** wVtbl = *(void***)walker;
                var getParent = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)wVtbl[3];
                if (getParent(walker, cur, &next) != 0)
                    next = IntPtr.Zero;
            }
            catch { next = IntPtr.Zero; }
            Marshal.Release(cur);
            cur = next;
        }
        if (cur != IntPtr.Zero) Marshal.Release(cur);
        return IntPtr.Zero;
    }

    private static IntPtr GetSelectionRangeArray(out bool supported, int? verifyMouseX = null, int? verifyMouseY = null)
    {
        supported = false;
        bool ptSupported = false;
        if (verifyMouseX.HasValue && verifyMouseY.HasValue)
        {
            IntPtr ptEl = GetElementFromPoint(verifyMouseX.Value, verifyMouseY.Value);
            if (ptEl != IntPtr.Zero)
            {
                try
                {
                    IntPtr ptArr = TryGetSelectionFromElementOrAncestors(ptEl, out ptSupported);
                    if (ptArr != IntPtr.Zero)
                    {
                        supported = true;
                        return ptArr;
                    }
                }
                finally { Marshal.Release(ptEl); }
            }
        }

        IntPtr focusEl = GetFocusedElement();
        if (focusEl != IntPtr.Zero)
        {
            try
            {
                IntPtr focusArr = TryGetSelectionFromElementOrAncestors(focusEl, out bool focusSupported);
                supported = ptSupported || focusSupported;
                return focusArr;
            }
            finally { Marshal.Release(focusEl); }
        }

        supported = ptSupported;
        return IntPtr.Zero;
    }

    public static string GetUiaSelectionText(int maxChars, out bool supported, int? verifyMouseX = null, int? verifyMouseY = null)
    {
        IntPtr arr = GetSelectionRangeArray(out supported, verifyMouseX, verifyMouseY);
        if (arr == IntPtr.Zero) return "";
        var sb = new StringBuilder();
        bool nearMouse = !verifyMouseX.HasValue || !verifyMouseY.HasValue;
        bool hadAnyBoundingRect = false;
        try
        {
            void** arrVtbl = *(void***)arr;
            var getLen = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)arrVtbl[3];
            var getEl  = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)arrVtbl[4];
            int count = 0;
            if (getLen(arr, &count) == 0 && count > 0)
            {
                int limit = Math.Min(count, 5);
                for (int i = 0; i < limit; i++)
                {
                    IntPtr rng = IntPtr.Zero;
                    if (getEl(arr, i, &rng) == 0 && rng != IntPtr.Zero)
                    {
                        try
                        {
                            void** rngVtbl = *(void***)rng;
                            if (!nearMouse && TryCheckRangeProximity(rng, verifyMouseX.Value, verifyMouseY.Value, out bool hasRects, out bool isNear))
                            {
                                if (hasRects) hadAnyBoundingRect = true;
                                if (isNear) nearMouse = true;
                            }
                            var getText = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)rngVtbl[12];
                            IntPtr bstr = IntPtr.Zero;
                            if (getText(rng, maxChars + 1, &bstr) == 0 && bstr != IntPtr.Zero)
                            {
                                string s = Marshal.PtrToStringBSTR(bstr);
                                Native.SysFreeString(bstr);
                                if (!string.IsNullOrEmpty(s))
                                {
                                    if (sb.Length > 0) sb.Append('\n');
                                    sb.Append(s);
                                }
                            }
                        }
                        finally { Marshal.Release(rng); }
                    }
                    if (sb.Length > maxChars) break;
                }
            }
        }
        catch { }
        finally { Marshal.Release(arr); }

        // If the UIA range reported valid screen bounding rectangles, but the mouse-up point was far away from all of them,
        // ignore it as a stale selection left over from earlier on the page.
        if (hadAnyBoundingRect && !nearMouse)
            return "";

        return sb.ToString();
    }

    private static bool TryCheckRangeProximity(IntPtr rng, int mx, int my, out bool hasRects, out bool isNear)
    {
        hasRects = false;
        isNear = false;
        try
        {
            void** rngVtbl = *(void***)rng;
            var getRects = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)rngVtbl[10];
            IntPtr psa = IntPtr.Zero;
            if (getRects(rng, &psa) == 0 && psa != IntPtr.Zero)
            {
                try
                {
                    Native.SafeArrayGetLBound(psa, 1, out int lb);
                    Native.SafeArrayGetUBound(psa, 1, out int ub);
                    int len = ub - lb + 1;
                    if (len >= 4 && Native.SafeArrayAccessData(psa, out IntPtr pData) == 0)
                    {
                        double* d = (double*)pData;
                        double minY = double.MaxValue, maxY = double.MinValue;
                        double minX = double.MaxValue, maxX = double.MinValue;
                        double scale = Native.ScaleAt(mx, my);
                        double tolX = 220 * scale;
                        double tolY = 140 * scale;

                        for (int idx = 0; idx + 3 < len; idx += 4)
                        {
                            double rx = d[idx], ry = d[idx + 1], rw = d[idx + 2], rh = d[idx + 3];
                            if (rw <= 0 && rh <= 0) continue;
                            hasRects = true;
                            if (rx < minX) minX = rx;
                            if (rx + rw > maxX) maxX = rx + rw;
                            if (ry < minY) minY = ry;
                            if (ry + rh > maxY) maxY = ry + rh;

                            if (mx >= rx - tolX && mx <= rx + rw + tolX &&
                                my >= ry - tolY && my <= ry + rh + tolY)
                            {
                                isNear = true;
                                break;
                            }
                        }
                        if (hasRects && !isNear)
                        {
                            // Also allow anywhere inside the overall multi-line bounding box + tolerance
                            if (mx >= minX - tolX && mx <= maxX + tolX &&
                                my >= minY - tolY && my <= maxY + tolY)
                            {
                                isNear = true;
                            }
                        }
                        Native.SafeArrayUnaccessData(psa);
                    }
                }
                finally { Native.SafeArrayDestroy(psa); }
            }
            return true;
        }
        catch { return false; }
    }

    public static bool TryGetSelectionAnchor(out int ax, out int ay)
    {
        ax = 0; ay = 0;
        IntPtr arr = GetSelectionRangeArray(out _);
        if (arr == IntPtr.Zero) return false;
        bool ok = false;
        try
        {
            void** arrVtbl = *(void***)arr;
            var getLen = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)arrVtbl[3];
            var getEl  = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)arrVtbl[4];
            int count = 0;
            if (getLen(arr, &count) == 0 && count > 0)
            {
                IntPtr rng = IntPtr.Zero;
                if (getEl(arr, 0, &rng) == 0 && rng != IntPtr.Zero)
                {
                    try
                    {
                        void** rngVtbl = *(void***)rng;
                        var getRects = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)rngVtbl[10];
                        IntPtr psa = IntPtr.Zero;
                        if (getRects(rng, &psa) == 0 && psa != IntPtr.Zero)
                        {
                            try
                            {
                                Native.SafeArrayGetLBound(psa, 1, out int lb);
                                Native.SafeArrayGetUBound(psa, 1, out int ub);
                                if (ub - lb + 1 >= 4 && Native.SafeArrayAccessData(psa, out IntPtr pData) == 0)
                                {
                                    double* d = (double*)pData;
                                    double rx = d[0], ry = d[1], rw = d[2];
                                    Native.SafeArrayUnaccessData(psa);
                                    if (rw > 0)
                                    {
                                        ax = (int)Math.Round(rx + rw / 2.0);
                                        ay = (int)Math.Round(ry);
                                        ok = true;
                                    }
                                }
                            }
                            finally { Native.SafeArrayDestroy(psa); }
                        }
                    }
                    finally { Marshal.Release(rng); }
                }
            }
        }
        catch { }
        finally { Marshal.Release(arr); }
        return ok;
    }

    public static bool IsEditableFocus(bool showEditWhenUnknown = true)
    {
        int r = -1;
        IntPtr el = GetFocusedElement();
        if (el != IntPtr.Zero)
        {
            try
            {
                if (GetProperty(el, 30043) is int hasVal && hasVal == 1)
                    r = (GetProperty(el, 30046) is int ro && ro == 1) ? 0 : 1;
                else if (GetProperty(el, 30003) is int ct)
                {
                    if (ct == 50004) r = 1;
                    else if (ReadOnlyControlTypes.Contains(ct)) r = 0;
                }
            }
            catch { }
            finally { Marshal.Release(el); }
        }

        if (r == -1)
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg != IntPtr.Zero)
            {
                uint tid = Native.GetWindowThreadProcessId(fg, out _);
                var gti = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf<Native.GUITHREADINFO>() };
                if (Native.GetGUIThreadInfo(tid, ref gti) && gti.hwndFocus != IntPtr.Zero)
                {
                    string cls = Native.ClassOf(gti.hwndFocus);
                    if (System.Text.RegularExpressions.Regex.IsMatch(cls, @"^(Edit|RichEdit.*|Scintilla|TEdit|TMemo|TRichEdit|ComboBox|ATL:Edit)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        long style = Native.GetWindowLongPtr(gti.hwndFocus, Native.GWL_STYLE).ToInt64();
                        r = (style & 0x800) != 0 ? 0 : 1;
                    }
                    else if (System.Text.RegularExpressions.Regex.IsMatch(cls, @"^(Chrome_|Mozilla|Internet Explorer_Server|SysListView|SysTreeView|Static|SysLink)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        r = 0;
                    }
                }
            }
        }
        return r == -1 ? showEditWhenUnknown : (r == 1);
    }

    public static void ResolveKeyboardAnchor(out int ax, out int ay)
    {
        if (TryGetSelectionAnchor(out ax, out ay)) return;
        IntPtr fg = Native.GetForegroundWindow();
        if (fg != IntPtr.Zero)
        {
            uint tid = Native.GetWindowThreadProcessId(fg, out _);
            var gti = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf<Native.GUITHREADINFO>() };
            if (Native.GetGUIThreadInfo(tid, ref gti) && gti.hwndCaret != IntPtr.Zero)
            {
                var pt = new Native.POINT(gti.rcCaret.Left, gti.rcCaret.Top);
                if (Native.ClientToScreen(gti.hwndCaret, ref pt) && (pt.X != 0 || pt.Y != 0))
                {
                    ax = pt.X; ay = pt.Y;
                    return;
                }
            }
        }
        Native.GetCursorPos(out var cur);
        ax = cur.X; ay = cur.Y;
    }

    // ---------------- Fast Clipboard Probe Fallback ----------------
    public static string GetSelectionText(ConfigService cfg, int? verifyMouseX = null, int? verifyMouseY = null)
    {
        string text = GetUiaSelectionText(cfg.MaxChars, out bool supported, verifyMouseX, verifyMouseY);
        if (supported && (!string.IsNullOrEmpty(text) || !cfg.CopyFallbackWhenUiaEmpty))
            return text;
        return cfg.UseCopyFallback ? ProbeCopy(cfg) : "";
    }

    public static string ProbeCopy(ConfigService cfg)
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return "";

        uint tid = Native.GetWindowThreadProcessId(fg, out _);
        var gti = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf<Native.GUITHREADINFO>() };
        if (Native.GetGUIThreadInfo(tid, ref gti) && gti.hwndFocus != IntPtr.Zero)
        {
            long style = Native.GetWindowLongPtr(gti.hwndFocus, Native.GWL_STYLE).ToInt64();
            if ((style & Native.ES_PASSWORD) != 0) return "";
        }

        if (Native.IsKeyDown(Native.VK_LBUTTON)) return "";

        // Fast 1-call text backup (never call EnumClipboardFormats which triggers slow delayed RTF/HTML rendering!)
        bool hadText = Native.IsClipboardFormatAvailable(Native.CF_UNICODETEXT);
        bool hadNonText = Native.IsClipboardFormatAvailable(Native.CF_HDROP) ||
                          Native.IsClipboardFormatAvailable(Native.CF_BITMAP) ||
                          Native.IsClipboardFormatAvailable(Native.CF_DIB);
        string oldText = hadText ? GetClipboardUnicodeText() : null;

        var historyBaseline = WinRtClipboardHistoryCleaner.SnapshotHistoryIds();
        long probeStartUtcFt = DateTime.UtcNow.ToFileTimeUtc() - TimeSpan.FromSeconds(2).Ticks;

        uint seqBefore = Native.GetClipboardSequenceNumber();
        SendCtrlKey('C');

        int timeoutMs = Math.Min(180, (int)(cfg.CopyTimeoutSec * 1000));
        int waited = 0;
        bool got = false;
        while (waited < timeoutMs)
        {
            Thread.Sleep(3);
            waited += 3;
            if (Native.GetClipboardSequenceNumber() != seqBefore)
            {
                got = true;
                break;
            }
        }

        if (!got) return "";

        bool isFiles = Native.IsClipboardFormatAvailable(Native.CF_HDROP) && !Native.IsClipboardFormatAvailable(Native.CF_UNICODETEXT);
        string result = !isFiles ? GetClipboardUnicodeText() : "";

        if (hadText && oldText != null)
            SetClipboardUnicodeText(oldText, excludeFromHistory: true);
        else if (!hadText && !isFiles && !hadNonText)
            EmptyClipboardSafe();

        if (historyBaseline != null)
            WinRtClipboardHistoryCleaner.PurgeNewHistoryItemsAsync(historyBaseline, probeStartUtcFt);

        if (isFiles || string.IsNullOrEmpty(result)) return "";

        string exe = Native.ProcessNameOf(fg);
        if (cfg.LineCopyExes.Contains(exe) && (result.EndsWith("\n") || result.EndsWith("\r")))
            return "";

        return result;
    }

    /// <summary>
    /// Removes transient Ctrl+C probe entries from Windows 11 Clipboard History (Win+V) via combase/WinRT
    /// without external WinRT NuGet dependencies or blocking selection response.
    /// </summary>
    internal static class WinRtClipboardHistoryCleaner
    {
        private static readonly Guid IID_IClipboardStatics2 = new("D2AC1B6A-D29F-554B-B303-F0452345FE02");
        private static readonly Guid IID_IAsyncInfo         = new("00000036-0000-0000-C000-000000000046");

        private static IntPtr GetClipboardStatics2()
        {
            try
            {
                Native.RoInitialize(1 /*RO_INIT_MULTITHREADED*/);
                const string cls = "Windows.ApplicationModel.DataTransfer.Clipboard";
                fixed (char* pCls = cls)
                {
                    if (Native.WindowsCreateStringReference(pCls, (uint)cls.Length, out var hdr, out IntPtr hCls) == 0 && hCls != IntPtr.Zero)
                    {
                        if (Native.RoGetActivationFactory(hCls, in IID_IClipboardStatics2, out IntPtr fac) == 0)
                            return fac;
                    }
                }
            }
            catch { }
            return IntPtr.Zero;
        }

        public static bool IsHistoryEnabled()
        {
            IntPtr stat = GetClipboardStatics2();
            if (stat == IntPtr.Zero) return false;
            try
            {
                void** vtbl = *(void***)stat;
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)vtbl[10];
                byte enabled = 0;
                return fn(stat, &enabled) == 0 && enabled != 0;
            }
            catch { return false; }
            finally { Marshal.Release(stat); }
        }

        public static HashSet<string> SnapshotHistoryIds()
        {
            if (Native.OsBuild < 17763) return null;
            IntPtr stat = GetClipboardStatics2();
            if (stat == IntPtr.Zero) return null;
            try
            {
                void** vtbl = *(void***)stat;
                var isEn = (delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)vtbl[10];
                byte enabled = 0;
                if (isEn(stat, &enabled) != 0 || enabled == 0) return null;

                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in FetchHistoryItems(stat, maxItems: 25))
                {
                    if (!string.IsNullOrEmpty(entry.Id)) set.Add(entry.Id);
                    Marshal.Release(entry.ItemPtr);
                }
                return set;
            }
            catch { return null; }
            finally { Marshal.Release(stat); }
        }

        public static void PurgeNewHistoryItemsAsync(HashSet<string> baselineIds, long minUtcFileTime)
        {
            if (baselineIds == null) return;
            _ = Task.Delay(65).ContinueWith(_ =>
            {
                try { DeleteNewItemsPass(baselineIds, minUtcFileTime); } catch { }
                _ = Task.Delay(210).ContinueWith(_2 =>
                {
                    try { DeleteNewItemsPass(baselineIds, minUtcFileTime); } catch { }
                }, TaskScheduler.Default);
            }, TaskScheduler.Default);
        }

        private static void DeleteNewItemsPass(HashSet<string> baselineIds, long minUtcFileTime)
        {
            IntPtr stat = GetClipboardStatics2();
            if (stat == IntPtr.Zero) return;
            try
            {
                void** vtbl = *(void***)stat;
                var delFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, byte*, int>)vtbl[8];
                foreach (var entry in FetchHistoryItems(stat, maxItems: 12))
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(entry.Id) && !baselineIds.Contains(entry.Id) && entry.TimestampFileTime >= minUtcFileTime)
                        {
                            byte removed = 0;
                            delFn(stat, entry.ItemPtr, &removed);
                        }
                    }
                    finally { Marshal.Release(entry.ItemPtr); }
                }
            }
            catch { }
            finally { Marshal.Release(stat); }
        }

        private static List<(IntPtr ItemPtr, string Id, long TimestampFileTime)> FetchHistoryItems(IntPtr stat, int maxItems)
        {
            var list = new List<(IntPtr, string, long)>();
            void** vtbl = *(void***)stat;
            var getHistAsync = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vtbl[6];
            IntPtr asyncOp = IntPtr.Zero;
            if (getHistAsync(stat, &asyncOp) != 0 || asyncOp == IntPtr.Zero) return list;

            try
            {
                IntPtr asyncInfo = IntPtr.Zero;
                Guid iidInfo = IID_IAsyncInfo;
                if (Marshal.QueryInterface(asyncOp, in iidInfo, out asyncInfo) != 0 || asyncInfo == IntPtr.Zero)
                    return list;

                int status = 0;
                try
                {
                    void** infoVtbl = *(void***)asyncInfo;
                    var getStatus = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)infoVtbl[7];
                    for (int i = 0; i < 40; i++)
                    {
                        if (getStatus(asyncInfo, &status) != 0 || status != 0) break;
                        Thread.Sleep(2);
                    }
                }
                finally { Marshal.Release(asyncInfo); }

                if (status != 1 /*Completed*/) return list;

                void** opVtbl = *(void***)asyncOp;
                var getResults = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)opVtbl[8];
                IntPtr res = IntPtr.Zero;
                if (getResults(asyncOp, &res) != 0 || res == IntPtr.Zero) return list;

                try
                {
                    void** resVtbl = *(void***)res;
                    var getResStatus = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)resVtbl[6];
                    int rStat = -1;
                    if (getResStatus(res, &rStat) != 0 || rStat != 0 /*Success*/) return list;

                    var getItems = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)resVtbl[7];
                    IntPtr vec = IntPtr.Zero;
                    if (getItems(res, &vec) != 0 || vec == IntPtr.Zero) return list;

                    try
                    {
                        void** vecVtbl = *(void***)vec;
                        var getAt   = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)vecVtbl[6];
                        var getSize = (delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)vecVtbl[7];
                        uint count = 0;
                        if (getSize(vec, &count) != 0 || count == 0) return list;

                        uint take = Math.Min(count, (uint)maxItems);
                        for (uint idx = 0; idx < take; idx++)
                        {
                            IntPtr item = IntPtr.Zero;
                            if (getAt(vec, idx, &item) == 0 && item != IntPtr.Zero)
                            {
                                void** itemVtbl = *(void***)item;
                                var getId = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)itemVtbl[6];
                                var getTs = (delegate* unmanaged[Stdcall]<IntPtr, long*, int>)itemVtbl[7];

                                string idStr = "";
                                IntPtr hId = IntPtr.Zero;
                                if (getId(item, &hId) == 0 && hId != IntPtr.Zero)
                                {
                                    IntPtr buf = Native.WindowsGetStringRawBuffer(hId, out uint len);
                                    if (buf != IntPtr.Zero && len > 0)
                                        idStr = Marshal.PtrToStringUni(buf, (int)len) ?? "";
                                    Native.WindowsDeleteString(hId);
                                }

                                long ts = 0;
                                getTs(item, &ts);
                                list.Add((item, idStr, ts));
                            }
                        }
                    }
                    finally { Marshal.Release(vec); }
                }
                finally { Marshal.Release(res); }
            }
            finally { Marshal.Release(asyncOp); }

            return list;
        }
    }

    public static string GetClipboardUnicodeText()
    {
        if (!Native.IsClipboardFormatAvailable(Native.CF_UNICODETEXT)) return "";
        if (!OpenClipboardRetry()) return "";
        try
        {
            IntPtr hMem = Native.GetClipboardData(Native.CF_UNICODETEXT);
            if (hMem == IntPtr.Zero) return "";
            IntPtr ptr = Native.GlobalLock(hMem);
            if (ptr == IntPtr.Zero) return "";
            try { return Marshal.PtrToStringUni(ptr) ?? ""; }
            finally { Native.GlobalUnlock(hMem); }
        }
        catch { return ""; }
        finally { Native.CloseClipboard(); }
    }

    public static bool SetClipboardUnicodeText(string text, bool excludeFromHistory = false)
    {
        text ??= "";
        if (!OpenClipboardRetry()) return false;
        try
        {
            Native.EmptyClipboard();
            if (excludeFromHistory)
            {
                SetClipboardDword(CfExcludeMonitor, 1);
                SetClipboardDword(CfCanIncludeHistory, 0);
                SetClipboardDword(CfCanUploadCloud, 0);
            }
            byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
            IntPtr hGlobal = Native.GlobalAlloc(Native.GMEM_MOVEABLE, new UIntPtr((uint)bytes.Length));
            if (hGlobal == IntPtr.Zero) return false;
            IntPtr ptr = Native.GlobalLock(hGlobal);
            if (ptr == IntPtr.Zero) { Native.GlobalFree(hGlobal); return false; }
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            Native.GlobalUnlock(hGlobal);
            if (Native.SetClipboardData(Native.CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
            {
                Native.GlobalFree(hGlobal);
                return false;
            }
            return true;
        }
        catch { return false; }
        finally { Native.CloseClipboard(); }
    }

    private static void SetClipboardDword(uint format, int value)
    {
        if (format == 0) return;
        IntPtr hGlobal = Native.GlobalAlloc(Native.GMEM_MOVEABLE, new UIntPtr(4));
        if (hGlobal == IntPtr.Zero) return;
        IntPtr ptr = Native.GlobalLock(hGlobal);
        if (ptr == IntPtr.Zero) { Native.GlobalFree(hGlobal); return; }
        Marshal.WriteInt32(ptr, value);
        Native.GlobalUnlock(hGlobal);
        if (Native.SetClipboardData(format, hGlobal) == IntPtr.Zero)
            Native.GlobalFree(hGlobal);
    }

    private static void EmptyClipboardSafe()
    {
        if (!OpenClipboardRetry()) return;
        try { Native.EmptyClipboard(); }
        finally { Native.CloseClipboard(); }
    }

    private static bool OpenClipboardRetry()
    {
        for (int i = 0; i < 5; i++)
        {
            if (Native.OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(4);
        }
        return false;
    }

    public static void SendCtrlKey(char upperLetter)
    {
        ushort vk = (ushort)char.ToUpperInvariant(upperLetter);
        var inputs = new Native.INPUT[4];
        inputs[0].type = Native.INPUT_KEYBOARD; inputs[0].U.ki.wVk = Native.VK_CONTROL;
        inputs[1].type = Native.INPUT_KEYBOARD; inputs[1].U.ki.wVk = vk;
        inputs[2].type = Native.INPUT_KEYBOARD; inputs[2].U.ki.wVk = vk; inputs[2].U.ki.dwFlags = Native.KEYEVENTF_KEYUP;
        inputs[3].type = Native.INPUT_KEYBOARD; inputs[3].U.ki.wVk = Native.VK_CONTROL; inputs[3].U.ki.dwFlags = Native.KEYEVENTF_KEYUP;
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
    }

    public static void SendUnicodeChar(char ch)
    {
        if (ch == '\n')
        {
            var enter = new Native.INPUT[2];
            enter[0].type = Native.INPUT_KEYBOARD; enter[0].U.ki.wVk = Native.VK_RETURN;
            enter[1].type = Native.INPUT_KEYBOARD; enter[1].U.ki.wVk = Native.VK_RETURN; enter[1].U.ki.dwFlags = Native.KEYEVENTF_KEYUP;
            Native.SendInput(2, enter, Marshal.SizeOf<Native.INPUT>());
            return;
        }
        var u = new Native.INPUT[2];
        u[0].type = Native.INPUT_KEYBOARD; u[0].U.ki.wScan = ch; u[0].U.ki.dwFlags = Native.KEYEVENTF_UNICODE;
        u[1].type = Native.INPUT_KEYBOARD; u[1].U.ki.wScan = ch; u[1].U.ki.dwFlags = Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP;
        Native.SendInput(2, u, Marshal.SizeOf<Native.INPUT>());
    }
}

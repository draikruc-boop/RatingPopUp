using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

        // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        private MusicBeeApiInterface mbApiInterface;
        private PluginInfo about = new PluginInfo();

        private RatingPopupForm popupForm;
        private string currentFileUrl = string.Empty;

        // Remembers where the user last dragged/sized the popup, so it opens the same way next time.
        private static Point? savedPosition = null;
        private static Size? savedSize = null;
        private static string SettingsFilePath =>
            Path.Combine(Path.GetTempPath(), "mb_RateMusic_popup_position.txt");

        // Polls playback position so we can show the popup near the end of a track
        // instead of at the start. MusicBee has no built-in "almost finished" event.
        private System.Threading.Timer positionPollTimer;
        private bool popupShownForCurrentTrack = false;
        private const int SECONDS_BEFORE_END = 30;
        private const int POLL_INTERVAL_MS = 1000;

        private const int POPUP_LEAD_MS = 150;   // small delay before showing, avoids flicker on rapid track skips
        private const int POPUP_VISIBLE_MS = 4000; // auto-hide after this long if the user doesn't interact

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            // Fixes DPI-scaling mismatches (mis-sized/cropped popup, wrong star clicked)
            // by telling Windows this process handles scaling itself.
            try { SetProcessDPIAware(); } catch { /* if MusicBee already set this, ignore */ }

            mbApiInterface = new MusicBeeApiInterface();
            mbApiInterface.Initialise(apiInterfacePtr);


            about.PluginInfoVersion = PluginInfoVersion;
            about.Name = "Half-Star Rating Popup";
            about.Description = "Shows a popup star-rating control on track change; supports half-star precision.";
            about.Author = "You";
            about.TargetApplication = "";
            about.Type = PluginType.General;
            about.VersionMajor = 1;
            about.VersionMinor = 0;
            about.Revision = 1;
            about.MinInterfaceVersion = MinInterfaceVersion;
            about.MinApiRevision = MinApiRevision;
            about.ReceiveNotifications = ReceiveNotificationFlags.PlayerEvents;
            about.ConfigurationPanelHeight = 0;

            LoadSavedPosition();

            return about;
        }

        private void LoadSavedPosition()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string[] parts = File.ReadAllText(SettingsFilePath).Split(',');
                    if (parts.Length >= 2 &&
                        int.TryParse(parts[0], out int x) &&
                        int.TryParse(parts[1], out int y))
                    {
                        savedPosition = new Point(x, y);
                    }
                    // Older versions of this file only stored x,y. If width/height are
                    // present too (from a version that supports resizing), use them.
                    if (parts.Length >= 4 &&
                        int.TryParse(parts[2], out int w) &&
                        int.TryParse(parts[3], out int h) &&
                        w > 0 && h > 0)
                    {
                        savedSize = new Size(w, h);
                    }
                }
            }
            catch
            {
                // If this fails, we just fall back to the default bottom-right position. Not critical.
            }
        }

        private static void SaveGeometry(Point location, Size size)
        {
            savedPosition = location;
            savedSize = size;
            try
            {
                File.WriteAllText(SettingsFilePath, $"{location.X},{location.Y},{size.Width},{size.Height}");
            }
            catch
            {
                // Non-critical: worst case, position/size just doesn't persist across restarts.
            }
        }

        public bool Configure(IntPtr panelHandle) => false;
        public void SaveSettings() { }
        public void Close(PluginCloseReason reason)
        {
            positionPollTimer?.Dispose();
            if (popupForm != null && !popupForm.IsDisposed)
            {
                try { popupForm.Invoke((Action)(() => popupForm.Close())); }
                catch { /* already closed (e.g. via the X button) - nothing to do */ }
            }
        }
        public void Uninstall()
        {
            positionPollTimer?.Dispose();
        }

        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            switch (type)
            {
                case NotificationType.PluginStartup:
                    // Start polling once, right after MusicBee finishes starting up.
                    if (positionPollTimer == null)
                    {
                        positionPollTimer = new System.Threading.Timer(
                            _ => CheckIfNearEndOfTrack(),
                            null,
                            POLL_INTERVAL_MS,
                            POLL_INTERVAL_MS
                        );
                    }
                    break;

                case NotificationType.TrackChanged:
                    currentFileUrl = sourceFileUrl;
                    popupShownForCurrentTrack = false; // reset so the new track can trigger its own popup

                    // If a popup from the previous (now finished) track is still open, close it.
                    if (popupForm != null && !popupForm.IsDisposed)
                    {
                        try { popupForm.Invoke((Action)(() => popupForm.Close())); }
                        catch { /* form may already be closing/disposed; safe to ignore */ }
                    }
                    break;
            }
        }

        private void CheckIfNearEndOfTrack()
        {
            try
            {
                if (string.IsNullOrEmpty(currentFileUrl) || popupShownForCurrentTrack)
                    return;

                if (mbApiInterface.Player_GetPlayState() != PlayState.Playing)
                    return;

                int durationMs = mbApiInterface.NowPlaying_GetDuration();
                int positionMs = mbApiInterface.Player_GetPosition();

                if (durationMs <= 0)
                    return;

                int remainingMs = durationMs - positionMs;

                if (remainingMs <= SECONDS_BEFORE_END * 1000 && remainingMs >= 0)
                {
                    popupShownForCurrentTrack = true;
                    ShowRatingPopupForCurrentTrack();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error in CheckIfNearEndOfTrack:\n" + ex.ToString(), "Debug - Exception");
            }
        }

        private void ShowRatingPopupForCurrentTrack()
        {
            string sourceFileUrl = currentFileUrl;
            if (string.IsNullOrEmpty(sourceFileUrl))
                return;

            string ratingText = mbApiInterface.Library_GetFileTag(sourceFileUrl, MetaDataType.Rating);

            // "Not rated" means the tag itself is empty/missing - not "parses to zero".
            // Checking the raw string (rather than a parsed numeric value) avoids edge cases
            // where GetFileTag returns something that happens to parse as 0 for an unrated
            // track, or a non-numeric placeholder some other tool wrote into the field.
            if (!string.IsNullOrWhiteSpace(ratingText))
                return;

            double currentRating = 0; // tag was empty, so nothing to convert - popup starts at no stars

            string artist = mbApiInterface.Library_GetFileTag(sourceFileUrl, MetaDataType.Artist);
            string title = mbApiInterface.Library_GetFileTag(sourceFileUrl, MetaDataType.TrackTitle);

            ShowPopup(sourceFileUrl, currentRating, artist, title);
        }

        // Loads the track's embedded/cached artwork as a standalone Bitmap (decoupled from
        // the original stream/bytes), or null if there isn't any.
        private Image LoadAlbumArt(string sourceFileUrl)
        {
            try
            {
                // This template's delegate takes out (not ref) parameters, so no initial values needed.
                bool success = mbApiInterface.Library_GetArtworkEx(
                    sourceFileUrl, 0, true, out PictureLocations pictureLocations, out string pictureUrl, out byte[] imageData);

                if (!success || imageData == null || imageData.Length == 0)
                    return null;

                using (var ms = new MemoryStream(imageData))
                using (var decoded = Image.FromStream(ms))
                {
                    return new Bitmap(decoded); // full copy, safe to use after the stream is disposed
                }
            }
            catch
            {
                return null; // missing/corrupt artwork shouldn't ever block the popup from showing
            }
        }

        private void ShowPopup(string fileUrl, double currentRating, string artist, string title)
        {
            Image albumArt = LoadAlbumArt(fileUrl);

            var thread = new System.Threading.Thread(() =>
            {
                // Must be called before any window is created on this thread.
                // This can override the process's DPI awareness (which MusicBee
                // already locked in before our plugin loaded) on a per-thread basis,
                // which is what actually fixes the cropped/mis-scaled rendering.
                try { SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); } catch { }

                popupForm = new RatingPopupForm(currentRating, artist, title, POPUP_VISIBLE_MS, savedPosition, savedSize, albumArt);
                popupForm.RatingSelected += (newRating) =>
                {
                    // Convert the 0-5 star value (0.5 increments) to the 0-100 scale
                    // the tag is stored in: 0.5 star = 10, 1 star = 20, ... 5 stars = 100.
                    int tagValue = (int)Math.Round(newRating * 20.0);
                    string ratingStr = tagValue.ToString(CultureInfo.InvariantCulture);
                    mbApiInterface.Library_SetFileTag(fileUrl, MetaDataType.Rating, ratingStr);
                    mbApiInterface.Library_CommitTagsToFile(fileUrl);
                };
                popupForm.GeometryChanged += (location, size) => SaveGeometry(location, size);
                popupForm.PreviousTrackRequested += () => mbApiInterface.Player_PlayPreviousTrack();
                popupForm.NextTrackRequested += () => mbApiInterface.Player_PlayNextTrack();
                popupForm.PlayPauseRequested += () => mbApiInterface.Player_PlayPause();
                popupForm.IsPlayingNow = () => mbApiInterface.Player_GetPlayState() == PlayState.Playing;
                popupForm.GetSpectrumData = (buffer) => mbApiInterface.NowPlaying_GetSpectrumData(buffer);

                Application.Run(popupForm);
            });

            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }
    }

    /// <summary>
    /// Small borderless popup with a 5-star control. Clicking the LEFT half of a star
    /// sets a half-star rating (e.g. 3.5); clicking the RIGHT half sets a full star (e.g. 4.0).
    /// </summary>
    public class RatingPopupForm : Form
    {
        public event Action<double> RatingSelected;

        // Plain Panel doesn't double-buffer, so redrawing it on every MouseMove (for the
        // hover preview) causes visible flicker/twitching. This subclass fixes that.
        private class DoubleBufferedPanel : Panel
        {
            public DoubleBufferedPanel()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;

        private const int StarCount = 5;

        // "Base" values are the layout at 1x scale, now sized to fit album art (a square
        // matching the full popup height) to the left of the title/stars/buttons column.
        private const int BaseWidth = 760;
        private const int BaseHeight = 360;
        private const int BaseStarSize = 50;
        private const int BaseStarSpacing = 10;
        private const int BaseLabelHeight = 76;
        private const int BaseBorderInset = 16;
        private const int BaseCornerRadius = 32;
        private const int BaseTopContentPadding = 8;
        private const int BaseStarVerticalNudge = 6;
        private const int BaseShadowExpand = 4;
        private const int BaseShadowOffsetX = 5;
        private const int BaseShadowOffsetY = 6;
        private const float BaseBorderWidth = 3f;
        private const float BaseTitleFontSize = 24f;
        private const float ArtSizeFactor = 0.82f; // art is slightly smaller than the full content height, and centered within it
        private const int BaseArtGap = 14;          // space between the album art and the text column
        private const int BaseArtLeftInset = 36;    // extra left margin in front of the art (bigger than the card's other edges)
        private const int BaseTitleLineGap = 24;    // gap between the title text and the divider line beneath it
        private const int BaseVisualizerHeight = 50; // spectrum strip under the transport buttons
        private const int BaseVisualizerGap = 10;
        private const int BaseCloseSize = 28; // includes room for the button's drop shadow
        private const int BaseButtonRowGap = 12;     // space between the stars and the transport buttons
        private const int BaseButtonRowHeight = 64;
        private const int BaseButtonSize = 50;
        private const int BaseButtonSpacing = 32;
        private const int BaseColumnRightInset = 56; // extra right-side margin (bigger than borderInset), keeps text/spectrum off the border
        private const int BaseTitleGap = 10; // minimum gap between the title and the star row (before stretching)
        private const int BaseStarRowHeight = 100; // natural (non-stretchy) height for the star row

        // Current scaled values, recomputed by RecalculateLayout() whenever the window resizes.
        private int starSize, starSpacing, labelHeight, borderInset,
            cornerRadius, topContentPadding, starVerticalNudge, shadowExpand, shadowOffsetX, shadowOffsetY,
            artGap, buttonRowGap, buttonRowHeight, buttonSize, buttonSpacing, artLeftInset, titleLineGap, visualizerHeight, visualizerGap, closeSize, starRowHeight, columnRightInset, titleGap;
        private float borderWidth;
        private Font titleFont;

        // Soft top-to-bottom shading for the card background.
        private static readonly Color GradientTop = Color.FromArgb(240, 240, 244);
        private static readonly Color GradientBottom = Color.FromArgb(208, 208, 214);
        private static readonly Color IconColor = Color.FromArgb(40, 40, 45);

        private double displayRating;   // what's shown (hover preview or committed value)
        private double committedRating; // the actual saved rating
        private readonly Timer topmostTimer;
        private readonly Panel infoPanel;
        private readonly string titleText;
        private readonly Panel starPanel;
        private readonly Panel artPanel;
        private readonly Panel prevButton;
        private readonly Panel playPauseButton;
        private readonly Panel nextButton;
        private readonly Panel visPanel;
        private readonly Panel closeButton;
        private readonly Timer visTimer;

        // Spectrum visualizer state. MusicBee fills a 1024-float FFT buffer; we group it into
        // BarCount log-spaced bands, auto-scale it (the API doesn't document a value range),
        // and smooth it so bars rise quickly and fall gently.
        private const int SpectrumBins = 1024;
        private const int BarCount = 32;
        private const int SpectrumAlpha = 51;  // 20% opacity (255 * 0.2)
        private Rectangle spectrumRegion;      // form-coordinate area the bars are drawn across
        private readonly float[] spectrumBuffer = new float[SpectrumBins];
        private readonly float[] barLevels = new float[BarCount];
        private float spectrumPeak = 0.001f;
        public Func<float[], int> GetSpectrumData; // wired up by Plugin.cs (owns mbApiInterface)
        private readonly Image albumArt;
        private ShadowForm shadowForm;

        // Fires after the user finishes dragging (mouse up) or resizing (ResizeEnd) the
        // popup, so the caller can persist both the position and size together.
        public event Action<Point, Size> GeometryChanged;

        // Transport controls: the popup doesn't talk to the MusicBee API directly, so the
        // caller (Plugin.cs, which owns mbApiInterface) wires these up.
        public event Action PreviousTrackRequested;
        public event Action NextTrackRequested;
        public event Action PlayPauseRequested;
        public Func<bool> IsPlayingNow; // queried fresh each time the play/pause icon repaints

        public RatingPopupForm(double currentRating, string artist, string title, int visibleMs,
            Point? savedPosition = null, Size? savedSize = null, Image albumArt = null)
        {
            this.albumArt = albumArt;
            committedRating = currentRating;
            displayRating = currentRating;

            AutoScaleMode = AutoScaleMode.None;
            AutoSize = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            DoubleBuffered = true; // reduces flicker from the gradient/border repainting
            BackColor = GradientBottom; // fallback; actual background is painted as a gradient below

            // A floor so the card can't be shrunk to where content overlaps or clips; no
            // MaximumSize, so it can be dragged as large as the user wants.
            MinimumSize = new Size(340, 165);
            Size = (savedSize.HasValue && savedSize.Value.Width >= MinimumSize.Width && savedSize.Value.Height >= MinimumSize.Height)
                ? savedSize.Value
                : new Size(BaseWidth, BaseHeight);

            if (savedPosition.HasValue)
            {
                Location = savedPosition.Value;
            }
            else
            {
                var screen = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(screen.Right - Width - 20, screen.Bottom - Height - 20);
            }

            titleText = string.IsNullOrEmpty(artist) ? title : $"{artist} \u2013 {title}";
            infoPanel = new DoubleBufferedPanel();
            // Panel/Label BackColor = Transparent is unreliable in WinForms (they often just
            // paint themselves opaque instead of showing what's behind), so instead of relying
            // on that, we draw this control's own exact slice of the form's gradient ourselves.
            infoPanel.Paint += (s, e) =>
            {
                PaintGradientSlice(e.Graphics, infoPanel);

                // Reserve titleLineGap at the bottom as clear space before the divider,
                // instead of centering the text across the whole panel height (which
                // left the line sitting almost directly under the text).
                var textRect = new Rectangle(0, 0, infoPanel.Width, Math.Max(1, infoPanel.Height - titleLineGap));
                TextRenderer.DrawText(
                    e.Graphics,
                    titleText,
                    titleFont ?? SystemFonts.DefaultFont,
                    textRect,
                    Color.FromArgb(28, 28, 30),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

                // A short, centered divider under the title (about half the panel width).
                using (var dividerPen = new Pen(Color.FromArgb(70, 0, 0, 0), 1f))
                {
                    int lineY = infoPanel.Height - 1;
                    int lineLength = (int)(infoPanel.Width * 0.55f);
                    int lineStartX = (infoPanel.Width - lineLength) / 2;
                    e.Graphics.DrawLine(dividerPen, lineStartX, lineY, lineStartX + lineLength, lineY);
                }
            };
            Controls.Add(infoPanel);

            // Album art: a square panel spanning the full content height, clipped to
            // slightly-rounded corners to match the card's overall style.
            artPanel = new DoubleBufferedPanel();
            artPanel.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                e.Graphics.CompositingQuality = CompositingQuality.HighQuality;

                var rect = artPanel.ClientRectangle;
                int radius = Math.Max(2, cornerRadius / 2);
                using (var clipPath = GetRoundedRectPath(rect, radius))
                {
                    var oldClip = e.Graphics.Clip;
                    e.Graphics.SetClip(clipPath, CombineMode.Intersect);

                    if (this.albumArt != null)
                    {
                        e.Graphics.DrawImage(this.albumArt, rect);
                    }
                    else
                    {
                        // Placeholder so it doesn't look broken when a track has no artwork.
                        using (var brush = new LinearGradientBrush(rect,
                                   Color.FromArgb(202, 202, 208), Color.FromArgb(172, 172, 180), LinearGradientMode.ForwardDiagonal))
                        {
                            e.Graphics.FillRectangle(brush, rect);
                        }
                    }

                    e.Graphics.Clip = oldClip;
                }

                using (var borderPen = new Pen(Color.FromArgb(55, 0, 0, 0), 1f))
                using (var borderPath = GetRoundedRectPath(rect, radius))
                {
                    e.Graphics.DrawPath(borderPen, borderPath);
                }
            };
            Controls.Add(artPanel);

            // Transport buttons: simple owner-drawn icons so they match the card's style.
            prevButton = MakeIconButton((g, r) => DrawSkipIcon(g, r, false), () => PreviousTrackRequested?.Invoke());
            nextButton = MakeIconButton((g, r) => DrawSkipIcon(g, r, true), () => NextTrackRequested?.Invoke());
            playPauseButton = MakeIconButton(
                (g, r) => DrawPlayPauseIcon(g, r, IsPlayingNow != null && IsPlayingNow()),
                () => { PlayPauseRequested?.Invoke(); playPauseButton.Invalidate(); });
            Controls.Add(prevButton);
            Controls.Add(playPauseButton);
            Controls.Add(nextButton);

            // Spectrum visualizer strip under the transport buttons.
            visPanel = new DoubleBufferedPanel();
            visPanel.Paint += VisPanel_Paint;
            Controls.Add(visPanel);
            visTimer = new Timer { Interval = 33 }; // ~30 fps

            // Close (X) button in the top-right corner. Added last and brought to the
            // front so it sits above the title panel it slightly overlaps.
            closeButton = MakeRaisedButton((g, r) => DrawCloseIcon(g, r), () => Close());
            Controls.Add(closeButton);
            closeButton.BringToFront();
            visTimer.Tick += (s, e) => { UpdateSpectrum(); RefreshSpectrumArea(); };

            // Drag-to-move: since this form has no title bar, we fake one by letting
            // the user click-and-drag anywhere on the info label or form background.
            bool dragging = false;
            Point dragStartPoint = Point.Empty;

            MouseEventHandler dragStart = (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    dragging = true;
                    dragStartPoint = e.Location;
                }
            };
            MouseEventHandler dragMove = (s, e) =>
            {
                if (dragging)
                {
                    Point diff = new Point(e.X - dragStartPoint.X, e.Y - dragStartPoint.Y);
                    Location = new Point(Location.X + diff.X, Location.Y + diff.Y);
                }
            };
            MouseEventHandler dragEnd = (s, e) =>
            {
                if (dragging)
                {
                    dragging = false;
                    GeometryChanged?.Invoke(Location, Size);
                }
            };

            infoPanel.MouseDown += dragStart;
            infoPanel.MouseMove += dragMove;
            infoPanel.MouseUp += dragEnd;
            MouseDown += dragStart;
            MouseMove += dragMove;
            MouseUp += dragEnd;

            starPanel = new DoubleBufferedPanel();
            starPanel.Paint += StarPanel_Paint;
            starPanel.MouseMove += StarPanel_MouseMove;
            starPanel.MouseLeave += (s, e) => { displayRating = committedRating; starPanel.Invalidate(); };
            starPanel.MouseClick += StarPanel_MouseClick;
            Controls.Add(starPanel);

            // Never auto-hides: stays open until the track ends (Plugin.cs closes it
            // when the next TrackChanged notification arrives).

            // TopMost = true only sets the flag once; other always-on-top windows
            // (or an app going fullscreen) can still push this popup behind them.
            // Periodically re-asserting HWND_TOPMOST via SetWindowPos keeps it pinned
            // above everything for as long as it's open.
            topmostTimer = new Timer { Interval = 1000 };
            topmostTimer.Tick += (s, e) =>
            {
                // Re-assert the shadow first, then the card, so the card always ends up
                // frontmost of the two (the most recent HWND_TOPMOST call wins ties).
                if (shadowForm != null && shadowForm.IsHandleCreated)
                    SetWindowPos(shadowForm.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                if (IsHandleCreated)
                    SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            };
            FormClosed += (s, e) =>
            {
                topmostTimer.Stop();
                topmostTimer.Dispose();
                visTimer.Stop();
                visTimer.Dispose();
                shadowForm?.Close();
                shadowForm?.Dispose();
                this.albumArt?.Dispose();
                titleFont?.Dispose();
            };

            // Dragging an edge/corner is handled via WM_NCHITTEST (see WndProc below), which
            // makes Windows run its normal interactive-resize loop - the same loop that fires
            // ResizeEnd on a regular window, so this is exactly "resize finished" for our purposes.
            ResizeEnd += (s, e) => GeometryChanged?.Invoke(Location, Size);

            shadowForm = new ShadowForm();
            RecalculateLayout();
        }

        // Native OS drop shadow for borderless windows (the same effect Windows uses
        // for tooltips/menus) - makes the card look like it's floating instead of flat.
        protected override CreateParams CreateParams
        {
            get
            {
                const int CS_DROPSHADOW = 0x00020000;
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= CS_DROPSHADOW;
                return cp;
            }
        }

        private static GraphicsPath GetRoundedRectPath(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // A small borderless window painted as a plain dark, semi-transparent rounded
        // silhouette, positioned just behind and below-right of the card. This gives a
        // clearly visible "dropped" shadow that doesn't depend on the OS/DWM honoring
        // CS_DROPSHADOW for a custom-Region window (which is often too subtle to notice).
        private class ShadowForm : Form
        {
            public ShadowForm()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                TopMost = true;
                BackColor = Color.Black;
                Opacity = 0.30;
                DoubleBuffered = true;
            }

            public void SetShape(int width, int height, int radius)
            {
                Size = new Size(width, height);
                using (var path = GetRoundedRectPath(new Rectangle(0, 0, width, height), radius))
                {
                    Region = new Region(path);
                }
            }
        }

        private void RepositionShadow()
        {
            if (shadowForm != null)
                shadowForm.Location = new Point(Location.X - shadowExpand + shadowOffsetX, Location.Y - shadowExpand + shadowOffsetY);
        }

        // Recomputes every scaled layout value (star size, fonts, insets, corner radius,
        // the rounded Region, and the shadow) from the window's current Width/Height.
        // Called once at construction and again on every resize.
        private void RecalculateLayout()
        {
            float scale = Math.Min((float)Width / BaseWidth, (float)Height / BaseHeight);
            scale = Math.Max(0.4f, Math.Min(3f, scale));

            starSize = Math.Max(20, (int)Math.Round(BaseStarSize * scale));
            starSpacing = Math.Max(2, (int)Math.Round(BaseStarSpacing * scale));
            labelHeight = Math.Max(20, (int)Math.Round(BaseLabelHeight * scale));
            borderInset = Math.Max(6, (int)Math.Round(BaseBorderInset * scale));
            cornerRadius = Math.Max(4, (int)Math.Round(BaseCornerRadius * scale));
            topContentPadding = Math.Max(0, (int)Math.Round(BaseTopContentPadding * scale));
            starVerticalNudge = (int)Math.Round(BaseStarVerticalNudge * scale);
            shadowExpand = Math.Max(2, (int)Math.Round(BaseShadowExpand * scale));
            shadowOffsetX = Math.Max(2, (int)Math.Round(BaseShadowOffsetX * scale));
            shadowOffsetY = Math.Max(2, (int)Math.Round(BaseShadowOffsetY * scale));
            borderWidth = Math.Max(1.5f, BaseBorderWidth * scale);
            artGap = Math.Max(4, (int)Math.Round(BaseArtGap * scale));
            artLeftInset = Math.Max(6, (int)Math.Round(BaseArtLeftInset * scale));
            titleLineGap = Math.Max(4, (int)Math.Round(BaseTitleLineGap * scale));
            visualizerHeight = Math.Max(16, (int)Math.Round(BaseVisualizerHeight * scale));
            visualizerGap = Math.Max(3, (int)Math.Round(BaseVisualizerGap * scale));
            closeSize = Math.Max(16, (int)Math.Round(BaseCloseSize * scale));
            buttonRowGap = Math.Max(2, (int)Math.Round(BaseButtonRowGap * scale));
            buttonRowHeight = Math.Max(20, (int)Math.Round(BaseButtonRowHeight * scale));
            buttonSize = Math.Max(16, (int)Math.Round(BaseButtonSize * scale));
            buttonSpacing = Math.Max(4, (int)Math.Round(BaseButtonSpacing * scale));
            starRowHeight = Math.Max(starSize + 16, (int)Math.Round(BaseStarRowHeight * scale));
            columnRightInset = Math.Max(borderInset, (int)Math.Round(BaseColumnRightInset * scale));
            titleGap = Math.Max(2, (int)Math.Round(BaseTitleGap * scale));

            float newFontSize = Math.Max(8f, BaseTitleFontSize * scale);
            if (titleFont == null || Math.Abs(titleFont.Size - newFontSize) > 0.4f)
            {
                var old = titleFont;
                titleFont = new Font("Segoe UI", newFontSize);
                old?.Dispose();
            }

            // Album art is a square, sized relative to the full content height, and
            // vertically centered as a block (so the leftover space - since the art is
            // only ArtSizeFactor of the full height - splits evenly above and below,
            // instead of collecting entirely below it). The text column stretches to
            // match this same top and bottom edge, so it's centered as one unit with the art.
            int artAreaHeight = Math.Max(1, Height - borderInset * 2);
            int artSize = Math.Max(1, (int)Math.Round(artAreaHeight * ArtSizeFactor));
            int contentTop = borderInset + Math.Max(0, (artAreaHeight - artSize) / 2);
            int artY = contentTop;
            int artBottom = artY + artSize;
            if (artPanel != null)
            {
                artPanel.Location = new Point(artLeftInset, artY);
                artPanel.Size = new Size(artSize, artSize);
            }

            // Everything else lives in a column to the right of the art, with a bigger
            // margin on the right than the art/border use, so text and the spectrum
            // strip don't crowd right up against the card's edge.
            int columnX = artLeftInset + artSize + artGap;
            int columnWidth = Math.Max(1, Width - columnX - columnRightInset);

            // Top-right, set in from the corner so it isn't jammed against the rounded edge.
            if (closeButton != null)
            {
                int closeMarginX = Math.Max(4, (int)Math.Round(10 * scale));
                int closeMarginY = Math.Max(1, (int)Math.Round(2 * scale));
                closeButton.Bounds = new Rectangle(
                    Width - borderInset - closeMarginX - closeSize, borderInset + closeMarginY, closeSize, closeSize);
            }

            // The column's content is stretched to span the artwork's exact height - top
            // edge to bottom edge - by distributing any extra vertical room across the
            // three gaps (title-to-stars, stars-to-buttons, buttons-to-spectrum) instead of
            // using fixed gaps. So the title's top always matches the art's top, and the
            // spectrum strip's bottom always matches the art's bottom.
            int fixedContentHeight = labelHeight + starRowHeight + buttonRowHeight + visualizerHeight;
            int minGapsTotal = titleGap + buttonRowGap + visualizerGap;
            int available = Math.Max(0, (artBottom - contentTop) - fixedContentHeight);
            int extra = Math.Max(0, available - minGapsTotal);
            int extraPerGap = extra / 3;
            int stretchedTitleGap = titleGap + extraPerGap;
            int stretchedButtonRowGap = buttonRowGap + extraPerGap;
            int stretchedVisualizerGap = visualizerGap + extraPerGap;

            if (infoPanel != null)
            {
                infoPanel.Location = new Point(columnX, contentTop);
                infoPanel.Size = new Size(columnWidth, labelHeight);
            }
            if (starPanel != null && infoPanel != null)
            {
                starPanel.Location = new Point(columnX, infoPanel.Bottom + stretchedTitleGap);
                starPanel.Size = new Size(columnWidth, starRowHeight);
            }

            // Transport buttons, centered as a row below the stars.
            if (starPanel != null)
            {
                int rowY = starPanel.Bottom + stretchedButtonRowGap;
                int buttonsTotalWidth = buttonSize * 3 + buttonSpacing * 2;
                int rowStartX = columnX + Math.Max(0, (columnWidth - buttonsTotalWidth) / 2);
                int buttonY = rowY + Math.Max(0, (buttonRowHeight - buttonSize) / 2);

                if (prevButton != null)
                    prevButton.Bounds = new Rectangle(rowStartX, buttonY, buttonSize, buttonSize);
                if (playPauseButton != null)
                    playPauseButton.Bounds = new Rectangle(rowStartX + buttonSize + buttonSpacing, buttonY, buttonSize, buttonSize);
                if (nextButton != null)
                    nextButton.Bounds = new Rectangle(rowStartX + (buttonSize + buttonSpacing) * 2, buttonY, buttonSize, buttonSize);

                // Spectrum strip below the buttons, spanning the text column's width, its
                // bottom edge landing on the album art's bottom edge.
                if (visPanel != null)
                    visPanel.Bounds = new Rectangle(columnX, rowY + buttonRowHeight + stretchedVisualizerGap, columnWidth, visualizerHeight);

                spectrumRegion = new Rectangle(columnX, starPanel.Top, columnWidth, Math.Max(1, artBottom - starPanel.Top));
            }

            using (var path = GetRoundedRectPath(new Rectangle(0, 0, Width, Height), cornerRadius))
            {
                Region = new Region(path);
            }

            if (shadowForm != null)
            {
                shadowForm.SetShape(Width + shadowExpand * 2, Height + shadowExpand * 2, cornerRadius + shadowExpand);
                RepositionShadow();
            }

            artPanel?.Invalidate();
            infoPanel?.Invalidate();
            starPanel?.Invalidate();
            prevButton?.Invalidate();
            playPauseButton?.Invalidate();
            nextButton?.Invalidate();
            visPanel?.Invalidate();
            closeButton?.Invalidate();
            Invalidate();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            RecalculateLayout();
            shadowForm.Show();
            LocationChanged += (s, e2) => RepositionShadow();
            Invalidate(true);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // Guard against the resize events WinForms fires while the form is still
            // being constructed, before infoPanel/starPanel exist yet.
            if (infoPanel != null && starPanel != null)
                RecalculateLayout();
        }

        // FormBorderStyle.None means there's no OS-drawn border to grab for resizing, so we
        // manually report the edge/corner hit-test codes ourselves; Windows then runs its
        // normal interactive resize loop (cursor changes, live drag, ResizeEnd event) exactly
        // as it would for a regular window border.
        private const int WM_NCHITTEST = 0x0084;
        private const int HTCLIENT = 1;
        private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
            HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        private const int ResizeBorder = 8; // must stay <= the smallest borderInset can scale down to

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                if ((int)m.Result == HTCLIENT)
                {
                    int screenX = unchecked((short)((long)m.LParam & 0xFFFF));
                    int screenY = unchecked((short)(((long)m.LParam >> 16) & 0xFFFF));
                    Point pos = PointToClient(new Point(screenX, screenY));

                    bool onLeft = pos.X <= ResizeBorder;
                    bool onRight = pos.X >= Width - ResizeBorder;
                    bool onTop = pos.Y <= ResizeBorder;
                    bool onBottom = pos.Y >= Height - ResizeBorder;

                    if (onTop && onLeft) m.Result = (IntPtr)HTTOPLEFT;
                    else if (onTop && onRight) m.Result = (IntPtr)HTTOPRIGHT;
                    else if (onBottom && onLeft) m.Result = (IntPtr)HTBOTTOMLEFT;
                    else if (onBottom && onRight) m.Result = (IntPtr)HTBOTTOMRIGHT;
                    else if (onLeft) m.Result = (IntPtr)HTLEFT;
                    else if (onRight) m.Result = (IntPtr)HTRIGHT;
                    else if (onTop) m.Result = (IntPtr)HTTOP;
                    else if (onBottom) m.Result = (IntPtr)HTBOTTOM;
                }
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.CompositingQuality = CompositingQuality.HighQuality;

            var outerRect = new Rectangle(0, 0, Width, Height);

            using (var outerPath = GetRoundedRectPath(outerRect, cornerRadius))
            using (var borderPen = new Pen(Color.FromArgb(150, 150, 156), borderWidth) { Alignment = PenAlignment.Inset })
            {
                e.Graphics.DrawPath(borderPen, outerPath);
            }

            // A thin, semi-transparent highlight just inside the border gives the
            // card a touch of glassy depth instead of a flat single-tone edge.
            var innerRect = Rectangle.Inflate(outerRect, -(int)borderWidth - 1, -(int)borderWidth - 1);
            int innerRadius = Math.Max(1, cornerRadius - (int)borderWidth);
            using (var innerPath = GetRoundedRectPath(innerRect, innerRadius))
            using (var highlightPen = new Pen(Color.FromArgb(70, 255, 255, 255), 1f))
            {
                e.Graphics.DrawPath(highlightPen, innerPath);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Soft top-to-bottom gradient instead of a flat fill; the Region already
            // clips this to the rounded card shape, so painting the full rectangle is fine.
            using (var brush = new LinearGradientBrush(new Rectangle(0, 0, Width, Math.Max(1, Height)),
                       GradientTop, GradientBottom, LinearGradientMode.Vertical))
            {
                e.Graphics.FillRectangle(brush, new Rectangle(0, 0, Width, Height));
            }
            DrawSpectrum(e.Graphics, ClientRectangle);
        }

        // Paints the portion of the form's overall gradient that falls behind a given
        // child control, using that control's own Graphics context (whose (0,0) is the
        // control's top-left, not the form's). Building the brush's rectangle offset by
        // -control.Top and sized to the full form height makes the two line up exactly,
        // so the label/star panel appear to share one continuous background.
        private void PaintGradientSlice(Graphics g, Control control)
        {
            var rect = new Rectangle(0, -control.Top, Math.Max(1, control.Width), Math.Max(1, Height));
            using (var brush = new LinearGradientBrush(rect, GradientTop, GradientBottom, LinearGradientMode.Vertical))
            {
                g.FillRectangle(brush, control.ClientRectangle);
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RecalculateLayout();
            Refresh();

            if (shadowForm != null && shadowForm.IsHandleCreated)
                SetWindowPos(shadowForm.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            topmostTimer.Start();
            visTimer.Start();
        }

        // Total width of the row of stars at the current scaled size, and the x-offset
        // that centers that row within the star panel's actual width. Shared by both
        // drawing and hit-testing so a click always lands on the star it looks like it did.
        private int StarRowWidth => StarCount * starSize + (StarCount - 1) * starSpacing;
        private int StarRowStartX => Math.Max(0, (starPanel.Width - StarRowWidth) / 2);

        // Given an x-coordinate within the star row, return the rating value
        // (in 0.5 increments) that point corresponds to. Uses the current scaled
        // starSize/starSpacing and the row's centered start position, so it always
        // matches what's on screen.
        private double RatingFromX(int x)
        {
            int stepWidth = starSize + starSpacing;
            int adjustedX = x - StarRowStartX; // account for the same centering DrawStar uses

            if (adjustedX < 0) return 0;

            int index = adjustedX / stepWidth;          // which star (0-based)
            if (index >= StarCount) return StarCount;

            int xWithinStar = adjustedX - (index * stepWidth);
            bool leftHalf = xWithinStar < (starSize / 2);

            return leftHalf ? index + 0.5 : index + 1.0;
        }

        private void StarPanel_MouseMove(object sender, MouseEventArgs e)
        {
            displayRating = RatingFromX(e.X);
            starPanel.Invalidate();
        }

        private void StarPanel_MouseClick(object sender, MouseEventArgs e)
        {
            committedRating = RatingFromX(e.X);
            displayRating = committedRating;
            starPanel.Invalidate();

            RatingSelected?.Invoke(committedRating);
            // Stays open after rating; Plugin.cs closes it when the track ends
            // (next TrackChanged notification), so the transport controls remain usable.
        }

        private void StarPanel_Paint(object sender, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.CompositingQuality = CompositingQuality.HighQuality;

            // Panel's own background paints opaque before this event fires (transparent
            // BackColor doesn't reliably work), so we deliberately paint over the whole
            // client area with our own matching gradient slice first.
            PaintGradientSlice(e.Graphics, starPanel);
            DrawSpectrum(e.Graphics, starPanel.Bounds);

            for (int i = 0; i < StarCount; i++)
            {
                int x = i * (starSize + starSpacing) + StarRowStartX;
                int y = (starPanel.Height - starSize) / 2 + starVerticalNudge;

                double starValue = displayRating - i; // how "filled" this star is: 0, 0.5, or 1+
                starValue = Math.Max(0, Math.Min(1, starValue));

                DrawStar(e.Graphics, x, y, starSize, starValue);
            }
        }

        private static readonly Color GoldLight = Color.FromArgb(255, 238, 130);
        private static readonly Color GoldDark = Color.FromArgb(198, 128, 0);
        private static readonly Color GoldOutline = Color.FromArgb(150, 92, 0);
        private static readonly Color EmptyLight = Color.FromArgb(238, 238, 242);
        private static readonly Color EmptyDark = Color.FromArgb(176, 176, 184);

        private void DrawStar(Graphics g, int x, int y, int size, double fillFraction)
        {
            PointF[] points = StarPoints(x, y, size);
            var center = new PointF(x + size / 2f, y + size / 2f);

            // Soft drop shadow, offset down-right, so the star looks raised off the card.
            float off = Math.Max(1f, size / 22f);
            var shadow = new PointF[points.Length];
            for (int i = 0; i < points.Length; i++)
                shadow[i] = new PointF(points[i].X + off * 0.6f, points[i].Y + off * 1.2f);
            using (var shadowBrush = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                g.FillPolygon(shadowBrush, shadow);

            // Empty (grey) star, faceted the same way as the gold one.
            DrawFacetedStar(g, points, center, EmptyDark, EmptyLight);
            using (var emptyPen = new Pen(Color.FromArgb(170, 150, 150, 158), 1f))
                g.DrawPolygon(emptyPen, points);

            if (fillFraction <= 0)
                return;

            // Gold star, clipped to the filled fraction (so half-stars show half gold).
            var oldClip = g.Clip;
            g.SetClip(new Rectangle(x, y, (int)Math.Round(size * fillFraction), size), CombineMode.Intersect);

            DrawFacetedStar(g, points, center, GoldDark, GoldLight);

            // Glossy highlight fading down from the top.
            using (var glossBrush = new LinearGradientBrush(new Rectangle(x, y, size, size),
                       Color.FromArgb(95, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
                g.FillPolygon(glossBrush, points);

            using (var goldPen = new Pen(GoldOutline, Math.Max(1f, size / 50f)) { LineJoin = LineJoin.Round })
                g.DrawPolygon(goldPen, points);

            g.Clip = oldClip;
            oldClip.Dispose();
        }

        // Draws a 5-point star as 10 triangular facets (two per point) lit from the top-left,
        // which is what gives it the raised, pyramid-like 3D look.
        private static void DrawFacetedStar(Graphics g, PointF[] pts, PointF center, Color dark, Color light)
        {
            // A mid-tone base underneath hides hairline seams between the anti-aliased facets.
            using (var baseBrush = new SolidBrush(LerpColor(dark, light, 0.5f)))
                g.FillPolygon(baseBrush, pts);

            const double lightAngle = -3 * Math.PI / 4; // light comes from the top-left
            for (int i = 0; i < 10; i += 2)             // even indices are the outer points
            {
                FillFacet(g, center, pts[i], pts[(i + 1) % 10], dark, light, lightAngle);
                FillFacet(g, center, pts[i], pts[(i + 9) % 10], dark, light, lightAngle);
            }
        }

        private static void FillFacet(Graphics g, PointF c, PointF a, PointF b, Color dark, Color light, double lightAngle)
        {
            float mx = (a.X + b.X + c.X) / 3f - c.X;
            float my = (a.Y + b.Y + c.Y) / 3f - c.Y;
            double facing = Math.Atan2(my, mx);

            float t = (float)(0.5 + 0.5 * Math.Cos(facing - lightAngle));
            t = Math.Max(0f, Math.Min(1f, t * 1.15f - 0.075f)); // slight contrast boost

            using (var brush = new SolidBrush(LerpColor(dark, light, t)))
                g.FillPolygon(brush, new[] { c, a, b });
        }

        private static Color LerpColor(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        // Standard 5-point star polygon inside a size x size box
        private PointF[] StarPoints(int x, int y, int size)
        {
            float cx = x + size / 2f;
            float cy = y + size / 2f;
            float outerR = size / 2f;
            float innerR = outerR * 0.4f;

            var pts = new PointF[10];
            for (int i = 0; i < 10; i++)
            {
                double angle = Math.PI / 2 * 3 + i * Math.PI / 5;
                float r = (i % 2 == 0) ? outerR : innerR;
                pts[i] = new PointF(
                    cx + (float)(r * Math.Cos(angle)),
                    cy + (float)(r * Math.Sin(angle))
                );
            }
            return pts;
        }

        // Reads the latest FFT data from MusicBee and updates the smoothed bar heights.
        private void UpdateSpectrum()
        {
            var targets = new float[BarCount];
            float frameMax = 0f;

            try
            {
                if (GetSpectrumData != null)
                {
                    GetSpectrumData(spectrumBuffer);

                    for (int i = 0; i < BarCount; i++)
                    {
                        // Log-spaced bands over bins 2..512, so bass gets as much room as treble.
                        int start = (int)(2 * Math.Pow(256, (double)i / BarCount));
                        int end = (int)(2 * Math.Pow(256, (double)(i + 1) / BarCount));
                        if (end <= start) end = start + 1;
                        if (end > SpectrumBins) end = SpectrumBins;

                        float peak = 0f;
                        for (int b = start; b < end; b++)
                        {
                            float v = Math.Abs(spectrumBuffer[b]);
                            if (!float.IsNaN(v) && v > peak) peak = v;
                        }

                        // Treble carries less energy than bass; tilt it up so the right side isn't flat.
                        peak *= 1f + 2f * i / BarCount;
                        targets[i] = peak;
                        if (peak > frameMax) frameMax = peak;
                    }
                }
            }
            catch
            {
                // No data this frame (e.g. nothing playing) - bars just fall back down.
            }

            // The API doesn't document a value range, so scale against a slowly-decaying peak.
            spectrumPeak = Math.Max(Math.Max(spectrumPeak * 0.995f, frameMax), 0.001f);

            for (int i = 0; i < BarCount; i++)
            {
                float level = (float)Math.Pow(Math.Min(1f, targets[i] / spectrumPeak), 0.8);
                if (level > barLevels[i])
                    barLevels[i] += (level - barLevels[i]) * 0.6f;  // rise fast
                else
                    barLevels[i] *= 0.88f;                          // fall gently
            }
        }

        private void VisPanel_Paint(object sender, PaintEventArgs e)
        {
            PaintGradientSlice(e.Graphics, visPanel);
            DrawSpectrum(e.Graphics, visPanel.Bounds);
        }

        // Repaints everything the bars sit behind: the form's own background (the gaps
        // between controls) plus each control that paints its slice of the bars.
        private void RefreshSpectrumArea()
        {
            Invalidate(spectrumRegion);
            starPanel?.Invalidate();
            prevButton?.Invalidate();
            playPauseButton?.Invalidate();
            nextButton?.Invalidate();
            visPanel?.Invalidate();
        }

        // Draws the spectrum bars, at 20% opacity, across spectrumRegion. Every control that
        // overlaps the region calls this with its own bounds (form coordinates), so the bars
        // line up across the whole area and the stars and buttons are drawn on top of them.
        private void DrawSpectrum(Graphics g, Rectangle targetBounds)
        {
            var region = spectrumRegion;
            if (region.Width < BarCount * 2 || region.Height < 4 || !region.IntersectsWith(targetBounds))
                return;

            var state = g.Save();
            g.TranslateTransform(-targetBounds.X, -targetBounds.Y);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int gap = Math.Max(2, region.Width / (BarCount * 4));
            float barW = (region.Width - gap * (BarCount - 1)) / (float)BarCount;

            // Same dark color as the transport-button icons, faded to 20% opacity.
            using (var brush = new SolidBrush(Color.FromArgb(SpectrumAlpha, IconColor)))
            {
                for (int i = 0; i < BarCount; i++)
                {
                    float h = Math.Max(2f, barLevels[i] * (region.Height - 2));
                    float x = region.X + i * (barW + gap);
                    var bar = new Rectangle((int)Math.Round(x), (int)Math.Round(region.Bottom - h), Math.Max(1, (int)barW), (int)Math.Round(h));

                    int radius = Math.Min(Math.Max(1, bar.Width / 2), bar.Height / 2);
                    if (radius >= 1 && bar.Width >= 4)
                    {
                        using (var path = GetRoundedRectPath(bar, radius))
                            g.FillPath(brush, path);
                    }
                    else
                    {
                        g.FillRectangle(brush, bar);
                    }
                }
            }

            g.Restore(state);
        }

        // Wraps an icon-drawing delegate into a clickable, hoverable owner-drawn button,
        // styled consistently with the rest of the card (shares its gradient background).
        private Panel MakeIconButton(Action<Graphics, Rectangle> paintIcon, Action onClick)
        {
            bool hovering = false;
            var btn = new DoubleBufferedPanel { Cursor = Cursors.Hand };
            btn.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                e.Graphics.CompositingQuality = CompositingQuality.HighQuality;

                PaintGradientSlice(e.Graphics, btn);
                DrawSpectrum(e.Graphics, btn.Bounds);

                var rect = btn.ClientRectangle;
                if (hovering)
                {
                    using (var hoverBrush = new SolidBrush(Color.FromArgb(35, 0, 0, 0)))
                    using (var hoverPath = GetRoundedRectPath(rect, Math.Max(2, rect.Height / 4)))
                    {
                        e.Graphics.FillPath(hoverBrush, hoverPath);
                    }
                }

                int pad = Math.Max(2, rect.Width / 5);
                var iconRect = Rectangle.Inflate(rect, -pad, -pad);
                paintIcon(e.Graphics, iconRect);
            };
            btn.MouseEnter += (s, e) => { hovering = true; btn.Invalidate(); };
            btn.MouseLeave += (s, e) => { hovering = false; btn.Invalidate(); };
            btn.Click += (s, e) => onClick?.Invoke();
            return btn;
        }

        // A round, raised 3D button: drop shadow, gradient body, glossy rim highlight and a
        // border. It visibly sinks (shadow shrinks, gradient flips) while the mouse is held down.
        private Panel MakeRaisedButton(Action<Graphics, Rectangle> paintIcon, Action onClick)
        {
            bool hovering = false, pressed = false;
            var btn = new DoubleBufferedPanel { Cursor = Cursors.Hand };
            btn.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;

                PaintGradientSlice(g, btn);

                int w = btn.Width, h = btn.Height;
                int sp = Math.Max(2, w / 9);                       // room reserved for the shadow
                int push = pressed ? Math.Max(1, sp / 2) : 0;      // pressed = sunk toward the shadow
                var body = new Rectangle(push, push, Math.Max(4, w - sp), Math.Max(4, h - sp));

                if (!pressed)
                {
                    var shadowRect = new Rectangle(body.X + sp / 2, body.Y + sp, body.Width, body.Height);
                    using (var shadowPath = GetRoundedRectPath(shadowRect, shadowRect.Width / 2))
                    using (var shadowBrush = new SolidBrush(Color.FromArgb(75, 0, 0, 0)))
                        g.FillPath(shadowBrush, shadowPath);
                }

                Color top = hovering ? Color.FromArgb(255, 255, 255) : Color.FromArgb(248, 248, 252);
                Color bottom = hovering ? Color.FromArgb(218, 218, 226) : Color.FromArgb(198, 198, 208);
                if (pressed) { var t = top; top = bottom; bottom = t; } // flip the light to look pushed in

                using (var bodyPath = GetRoundedRectPath(body, body.Width / 2))
                {
                    using (var bodyBrush = new LinearGradientBrush(body, top, bottom, LinearGradientMode.Vertical))
                        g.FillPath(bodyBrush, bodyPath);

                    // Glossy rim just inside the edge, brightest at the top.
                    var rim = Rectangle.Inflate(body, -2, -2);
                    if (rim.Width > 2 && rim.Height > 2)
                    {
                        using (var rimPath = GetRoundedRectPath(rim, rim.Width / 2))
                        using (var rimBrush = new LinearGradientBrush(rim, Color.FromArgb(pressed ? 60 : 210, 255, 255, 255),
                                   Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
                        using (var rimPen = new Pen(rimBrush, 1.5f))
                            g.DrawPath(rimPen, rimPath);
                    }

                    using (var borderPen = new Pen(Color.FromArgb(150, 130, 130, 142), 1.2f))
                        g.DrawPath(borderPen, bodyPath);
                }

                int pad = Math.Max(4, body.Width / 3); // extra padding shrinks the X specifically
                paintIcon(g, Rectangle.Inflate(body, -pad, -pad));
            };
            btn.MouseEnter += (s, e) => { hovering = true; btn.Invalidate(); };
            btn.MouseLeave += (s, e) => { hovering = false; pressed = false; btn.Invalidate(); };
            btn.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { pressed = true; btn.Invalidate(); } };
            btn.MouseUp += (s, e) => { pressed = false; btn.Invalidate(); };
            btn.Click += (s, e) => onClick?.Invoke();
            return btn;
        }

        private static void DrawPlayPauseIcon(Graphics g, Rectangle bounds, bool playing)
        {
            if (playing)
            {
                int barWidth = Math.Max(2, bounds.Width / 4);
                int gap = Math.Max(2, bounds.Width / 6);
                int totalW = barWidth * 2 + gap;
                int startX = bounds.X + (bounds.Width - totalW) / 2;
                using (var brush = new SolidBrush(IconColor))
                {
                    g.FillRectangle(brush, startX, bounds.Y, barWidth, bounds.Height);
                    g.FillRectangle(brush, startX + barWidth + gap, bounds.Y, barWidth, bounds.Height);
                }
            }
            else
            {
                float w = bounds.Width, h = bounds.Height;
                var pts = new[]
                {
                    new PointF(bounds.X + w * 0.22f, bounds.Y),
                    new PointF(bounds.X + w * 0.22f, bounds.Y + h),
                    new PointF(bounds.X + w * 0.85f, bounds.Y + h / 2f)
                };
                using (var brush = new SolidBrush(IconColor))
                {
                    g.FillPolygon(brush, pts);
                }
            }
        }

        private static void DrawCloseIcon(Graphics g, Rectangle bounds)
        {
            using (var pen = new Pen(IconColor, Math.Max(1.5f, bounds.Width / 7f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
                g.DrawLine(pen, bounds.Right, bounds.Top, bounds.Left, bounds.Bottom);
            }
        }

        // "Skip" icon: a triangle plus a bar, pointing left (previous) or right (next).
        private static void DrawSkipIcon(Graphics g, Rectangle bounds, bool pointingRight)
        {
            float w = bounds.Width, h = bounds.Height;
            using (var brush = new SolidBrush(IconColor))
            {
                if (pointingRight)
                {
                    var tri = new[]
                    {
                        new PointF(bounds.X + w * 0.12f, bounds.Y + h * 0.12f),
                        new PointF(bounds.X + w * 0.12f, bounds.Y + h * 0.88f),
                        new PointF(bounds.X + w * 0.62f, bounds.Y + h * 0.5f)
                    };
                    g.FillPolygon(brush, tri);
                    g.FillRectangle(brush, bounds.X + w * 0.68f, bounds.Y + h * 0.12f, w * 0.14f, h * 0.76f);
                }
                else
                {
                    var tri = new[]
                    {
                        new PointF(bounds.X + w * 0.88f, bounds.Y + h * 0.12f),
                        new PointF(bounds.X + w * 0.88f, bounds.Y + h * 0.88f),
                        new PointF(bounds.X + w * 0.38f, bounds.Y + h * 0.5f)
                    };
                    g.FillPolygon(brush, tri);
                    g.FillRectangle(brush, bounds.X + w * 0.18f, bounds.Y + h * 0.12f, w * 0.14f, h * 0.76f);
                }
            }
        }
    }
}
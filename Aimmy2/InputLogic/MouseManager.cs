using Aimmy2.Class;
using Aimmy2.MouseMovementLibraries.GHubSupport;
using Class;
using MouseMovementLibraries.ddxoftSupport;
using MouseMovementLibraries.RazerSupport;
using MouseMovementLibraries.SendInputSupport;
using MouseMovementLibraries.ArduinoSupport; // <<<< aus deiner alten Version
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;       // Interlocked, Thread.Sleep
using System.Diagnostics;

namespace InputLogic
{
    internal class MouseManager
    {
        private static readonly double ScreenWidth = WinAPICaller.ScreenWidth;
        private static readonly double ScreenHeight = WinAPICaller.ScreenHeight;

        private static DateTime LastClickTime = DateTime.MinValue;
        private static int LastAntiRecoilClickTime = 0;

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_MOVE = 0x0001;

        private static double previousX = 0;
        private static double previousY = 0;
        public static double smoothingFactor = 0.5;
        public static bool IsEMASmoothingEnabled = false;

        // >>> Neu: Arduino-Integration
        public static ArduinoInput arduinoMouse = new();
        private static volatile bool isArduinoReady = false;

        // >>> Neu: Schutzflagge für simulierte Klicks (für InputBindingManager)
        public static bool isSendingInput = false;

        // >>> Neu: Reentrancy-Schutz für TriggerClick, damit nichts doppelt feuert
        private static int isTriggerClickRunning = 0;

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);

        private static Random MouseRandom = new();

        // --- Initialisierung (einmalig beim App-Start aufrufen) ---
        public static void InitializeArduinoInBackground()
        {
            Debug.WriteLine("Starting background Arduino initialization...");
            Task.Run(() =>
            {
                bool success = false;
                try
                {
                    success = arduinoMouse.Initialize();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Arduino init failed: " + ex.Message);
                    success = false;
                }

                isArduinoReady = success;
                Debug.WriteLine(success
                    ? "Background Arduino initialization complete."
                    : "Background Arduino initialization failed. Will retry on first use.");
            });
        }

        public static void CheckArduinoConnectionStatus()
        {
            if (arduinoMouse == null)
            {
                Console.WriteLine("[Arduino] Not initialized (null reference).");
                System.Windows.MessageBox.Show(" Arduino input not initialized.",
                    "Arduino Status", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }

            if (!arduinoMouse.IsInitialized)
            {
                Console.WriteLine("[Arduino] Not connected – trying to connect again...");
                System.Windows.MessageBox.Show(" No Arduino connected.\n"
                    + "Please check your USB Cable or COM-Port.\n\n"
                    + "The software automatically attempts to connect to the Arduino on the next input.",
                    "Arduino not connected", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
            else
            {
                Console.WriteLine($"[Arduino]  is connected at Port {arduinoMouse.GetConnectedPort()}");
                System.Windows.MessageBox.Show($"Arduino successfully connected!\nPort: {arduinoMouse.GetConnectedPort()}",
                    "Arduino connected", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
        }


        private static Point CubicBezier(Point start, Point end, Point control1, Point control2, double t)
        {
            double u = 1 - t;
            double tt = t * t;
            double uu = u * u;

            double x = uu * u * start.X + 3 * uu * t * control1.X + 3 * u * tt * control2.X + tt * t * end.X;
            double y = uu * u * start.Y + 3 * uu * t * control1.Y + 3 * u * tt * control2.Y + tt * t * end.Y;

            if (IsEMASmoothingEnabled)
            {
                x = EmaSmoothing(previousX, x, smoothingFactor);
                y = EmaSmoothing(previousY, y, smoothingFactor);
            }

            previousX = x;
            previousY = y;

            return new Point((int)x, (int)y);
        }

        private static double EmaSmoothing(double previousValue, double currentValue, double smoothingFactor)
            => (currentValue * smoothingFactor) + (previousValue * (1 - smoothingFactor));

        public static async Task DoTriggerClick()
        {
            // Reentrancy-Schutz
            if (Interlocked.CompareExchange(ref isTriggerClickRunning, 1, 0) == 1)
                return;

            try
            {
                int timeSinceLastClick = (int)(DateTime.UtcNow - LastClickTime).TotalMilliseconds;
                int triggerDelayMilliseconds = (int)(Dictionary.sliderSettings["Auto Trigger Delay"] * 1000);
                const int clickDelayMilliseconds = 20;

                if (timeSinceLastClick < triggerDelayMilliseconds && LastClickTime != DateTime.MinValue)
                    return;

                string mouseMovementMethod = Dictionary.dropdownState["Mouse Movement Method"];

                switch (mouseMovementMethod)
                {
                    case "SendInput":
                        SendInputMouse.SendMouseCommand(MOUSEEVENTF_LEFTDOWN);
                        await Task.Delay(clickDelayMilliseconds);
                        SendInputMouse.SendMouseCommand(MOUSEEVENTF_LEFTUP);
                        break;

                    case "LG HUB":
                        LGMouse.Move(1, 0, 0, 0);
                        await Task.Delay(clickDelayMilliseconds);
                        LGMouse.Move(0, 0, 0, 0);
                        break;

                    case "Razer Synapse (Require Razer Peripheral)":
                        RZMouse.mouse_click(1);
                        await Task.Delay(clickDelayMilliseconds);
                        RZMouse.mouse_click(0);
                        break;

                    case "ddxoft Virtual Input Driver":
                        DdxoftMain.ddxoftInstance.btn!(1);
                        await Task.Delay(clickDelayMilliseconds);
                        DdxoftMain.ddxoftInstance.btn(2);
                        break;

                    case "Arduino (Serial HID Bridge)":
                        if (!isArduinoReady)
                            return;
                        // <<< WICHTIG >>> Flag setzen, damit InputBindingManager simulierte Klicks ignoriert
                        isSendingInput = true;
                        // Arduino-Protokoll: dx=0, dy=0, click=1 => kurzer LMB-Klick
                        arduinoMouse.SendMouseCommand(0, 0, 1);
                        await Task.Delay(50); // sicheres Zeitfenster (bewährt aus Altversion)
                        isSendingInput = false;
                        break;

                    default:
                        // Fallback: user32 mouse_event
                        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                        await Task.Delay(clickDelayMilliseconds);
                        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                        break;
                }

                LastClickTime = DateTime.UtcNow;
            }
            finally
            {
                Interlocked.Exchange(ref isTriggerClickRunning, 0);
            }
        }

        public static void DoAntiRecoil()
        {
            int timeSinceLastClick = Math.Abs(DateTime.UtcNow.Millisecond - LastAntiRecoilClickTime);

            if (timeSinceLastClick < Dictionary.AntiRecoilSettings["Fire Rate"])
                return;

            int xRecoil = (int)Dictionary.AntiRecoilSettings["X Recoil (Left/Right)"];
            int yRecoil = (int)Dictionary.AntiRecoilSettings["Y Recoil (Up/Down)"];

            switch (Dictionary.dropdownState["Mouse Movement Method"])
            {
                case "SendInput":
                    SendInputMouse.SendMouseCommand(MOUSEEVENTF_MOVE, xRecoil, yRecoil);
                    break;

                case "LG HUB":
                    LGMouse.Move(0, xRecoil, yRecoil, 0);
                    break;

                case "Razer Synapse (Require Razer Peripheral)":
                    RZMouse.mouse_move(xRecoil, yRecoil, true);
                    break;

                case "ddxoft Virtual Input Driver":
                    DdxoftMain.ddxoftInstance.movR!(xRecoil, yRecoil);
                    break;

                case "Arduino (Serial HID Bridge)":
                    if (!isArduinoReady) return;
                    // Kein Sleep/Flag nötig – das ist reine Bewegung
                    arduinoMouse.SendMouseCommand(xRecoil, yRecoil, 0);
                    break;

                default:
                    mouse_event(MOUSEEVENTF_MOVE, (uint)xRecoil, (uint)yRecoil, 0, 0);
                    break;
            }

            LastAntiRecoilClickTime = DateTime.UtcNow.Millisecond;
        }

        public static void MoveCrosshair(int detectedX, int detectedY)
        {
            int halfScreenWidth = (int)ScreenWidth / 2;
            int halfScreenHeight = (int)ScreenHeight / 2;

            int targetX = detectedX - halfScreenWidth;
            int targetY = detectedY - halfScreenHeight;

            double aspectRatioCorrection = ScreenWidth / ScreenHeight;

            int MouseJitter = (int)Dictionary.sliderSettings["Mouse Jitter"];
            int jitterX = MouseRandom.Next(-MouseJitter, MouseJitter);
            int jitterY = MouseRandom.Next(-MouseJitter, MouseJitter);

            Point start = new(0, 0);
            Point end = new(targetX, targetY);
            Point control1 = new(start.X + (end.X - start.X) / 3, start.Y + (end.Y - start.Y) / 3);
            Point control2 = new(start.X + 2 * (end.X - start.X) / 3, start.Y + 2 * (end.Y - start.Y) / 3);
            Point newPosition = CubicBezier(start, end, control1, control2, 1 - Dictionary.sliderSettings["Mouse Sensitivity (+/-)"]);

            targetX = Math.Clamp(targetX, -150, 150);
            targetY = Math.Clamp(targetY, -150, 150);

            targetY = (int)(targetY * aspectRatioCorrection);

            targetX += jitterX;
            targetY += jitterY;

            switch (Dictionary.dropdownState["Mouse Movement Method"])
            {
                case "SendInput":
                    SendInputMouse.SendMouseCommand(MOUSEEVENTF_MOVE, newPosition.X, newPosition.Y);
                    break;

                case "LG HUB":
                    LGMouse.Move(0, newPosition.X, newPosition.Y, 0);
                    break;

                case "Razer Synapse (Require Razer Peripheral)":
                    RZMouse.mouse_move(newPosition.X, newPosition.Y, true);
                    break;

                case "ddxoft Virtual Input Driver":
                    DdxoftMain.ddxoftInstance.movR!(newPosition.X, newPosition.Y);
                    break;

                case "Arduino (Serial HID Bridge)":
                    if (!isArduinoReady) return;
                    // reine relative Bewegung
                    arduinoMouse.SendMouseCommand(newPosition.X, newPosition.Y, 0);
                    break;

                default:
                    mouse_event(MOUSEEVENTF_MOVE, (uint)newPosition.X, (uint)newPosition.Y, 0, 0);
                    break;
            }

            if (Dictionary.toggleState["Auto Trigger"])
            {
                Task.Run(DoTriggerClick);
            }
        }
    }
}

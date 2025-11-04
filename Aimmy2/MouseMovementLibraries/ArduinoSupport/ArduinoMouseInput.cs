using System;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MouseMovementLibraries.ArduinoSupport
{
    internal class ArduinoInput
    {
        private SerialPort? _serialPort;
        private string? _foundComPort;
        private volatile bool _isInitialized = false;

        // schützt Initialize() vor paralleler Ausführung
        private readonly SemaphoreSlim _initGate = new(1, 1);

        // Optional: COM-Port via Umgebungsvariable erzwingen (z.B. "COM4")
        private const string ENV_COM_OVERRIDE = "AIMMY_ARDUINO_COM";

        // Geräte-Matches (international + typische Klone)
        private static readonly string[] DeviceNameHints =
        {
    // Englisch / International
    "USB Serial Device", "USB-Serial", "USB to Serial",

    // Deutsch
    "Serielles USB-Gerät", "USB-Seriell-Gerät",

    // Spanisch
    "Dispositivo serie USB", "Dispositivo serial USB",

    // Französisch
    "Périphérique série USB", "Périphérique série (USB)",

    // Italienisch
    "Dispositivo seriale USB",

    // Portugiesisch (PT/BR)
    "Dispositivo serial USB", "Dispositivo série USB",

    // Niederländisch
    "USB-serieel apparaat", "Serieel USB-apparaat",

    // Schwedisch / Norwegisch / Dänisch
    "USB-seriell enhet",          // sv, no (bokmål)
    "USB-seriell enhet",          // sv mit schmales geschütztes Leerzeichen
    "USB-seriel enhed",           // da
    "USB-serial enhed",           // da Variante

    // Polnisch / Tschechisch / Slowakisch / Ungarisch / Rumänisch
    "Urządzenie szeregowe USB",   // pl
    "Zařízení USB Serial",        // cs
    "Zariadenie USB Serial",      // sk
    "USB soros eszköz",           // hu
    "Dispozitiv serial USB",      // ro

    // Türkisch / Griechisch
    "USB Seri Aygıtı",            // tr
    "Συσκευή σειριακού USB",      // el

    // Russisch / Ukrainisch
    "USB-последовательное устройство", // ru
    "USB-послідовний пристрій",        // uk

    // Japanisch / Chinesisch / Koreanisch
    "USB シリアル デバイス",        // ja
    "USB 串口设备",                 // zh-CN
    "USB 串列裝置",                 // zh-TW
    "USB 시리얼 장치",              // ko

    // Häufige Chipsätze / Herstellerbezeichnungen (tauchen oft im Namen/Caption auf)
    "USB-SERIAL CH340", "CH340",
    "CP210", "CP210x",
    "PL2303",
    "FT232", "FTDI",
    "ATmega32U4",
    // Spezifisch Arduino
    "Arduino Leonardo", "Arduino Micro", "Arduino Uno", "Arduino Mega"
};

        public ArduinoInput()
        {
            Console.WriteLine("ArduinoInput created. Ready for background init.");
        }

        public bool IsInitialized => _isInitialized && _serialPort is { IsOpen: true };

        /// <summary>
        /// Kann im Background-Task aufgerufen werden (siehe MouseManager.InitializeArduinoInBackground()).
        /// Thread-safe und reentrant.
        /// </summary>
        public bool Initialize()
        {
            _initGate.Wait();
            try
            {
                if (IsInitialized) return true;

                // 1) Umgebungsvariable hat Vorrang
                var envCom = Environment.GetEnvironmentVariable(ENV_COM_OVERRIDE);
                if (!string.IsNullOrWhiteSpace(envCom))
                {
                    if (TryOpenPort(envCom.Trim()))
                    {
                        _foundComPort = envCom.Trim();
                        _isInitialized = true;
                        Console.WriteLine($"Arduino connected via ENV override: {_foundComPort}");
                        return true;
                    }
                    Console.WriteLine($"ENV {ENV_COM_OVERRIDE} set to '{envCom}', but open failed.");
                }

                // 2) Schnelle WMI-Suche
                var port = GetUsbSerialPortFast();
                if (port == null)
                {
                    // 3) Fallback: breite PnP-Suche
                    port = GetUsbSerialPortFallback();
                }

                if (port != null && TryOpenPort(port))
                {
                    _foundComPort = port;
                    _isInitialized = true;
                    Console.WriteLine($"Arduino connected: {_foundComPort}");
                    return true;
                }

                Console.WriteLine("Arduino not found or open failed.");
                _isInitialized = false;
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Initialize() failed: {ex.Message}");
                SafeClose();
                _isInitialized = false;
                return false;
            }
            finally
            {
                _initGate.Release();
            }
        }

        /// <summary>
        /// Für Aufrufe aus Move/Click Pfaden. Initialisiert bei Bedarf on-demand.
        /// </summary>
        private bool EnsureInitialized()
        {
            if (IsInitialized) return true;
            return Initialize();
        }

        private bool TryOpenPort(string comPort)
        {
            try
            {
                SafeClose();

                _serialPort = new SerialPort(comPort, 115200)
                {
                    Encoding = Encoding.ASCII,
                    NewLine = "\n",
                    DtrEnable = true,
                    RtsEnable = true,
                    WriteTimeout = 50,
                    ReadTimeout = 50,
                    Handshake = Handshake.None
                };

                _serialPort.Open();
                return _serialPort.IsOpen;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Open '{comPort}' failed: {ex.Message}");
                SafeClose();
                return false;
            }
        }

        /// <summary>
        /// Schnelle Suche über Win32_SerialPort (Name/Caption enthält Hints).
        /// </summary>
        private string? GetUsbSerialPortFast()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_SerialPort");
                foreach (ManagementObject dev in searcher.Get())
                {
                    var name = dev["Name"]?.ToString() ?? string.Empty;
                    var caption = dev["Caption"]?.ToString() ?? string.Empty;
                    var deviceId = dev["DeviceID"]?.ToString();

                    if (string.IsNullOrWhiteSpace(deviceId)) continue;

                    if (DeviceNameHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase) ||
                                                 caption.Contains(h, StringComparison.OrdinalIgnoreCase)))
                    {
                        Console.WriteLine($"Fast WMI: {name} on {deviceId}");
                        return deviceId; // "COMx"
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Fast WMI error: " + ex.Message);
            }

            Console.WriteLine("Fast WMI: No matching device.");
            return null;
        }

        /// <summary>
        /// Breiter Fallback über Win32_PnPEntity mit Regex auf "(COM\d+)".
        /// </summary>
        private string? GetUsbSerialPortFallback()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
                var regex = new Regex(@"\((COM\d+)\)", RegexOptions.IgnoreCase);

                foreach (ManagementObject dev in searcher.Get())
                {
                    var name = dev["Name"]?.ToString() ?? string.Empty;
                    if (!DeviceNameHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var m = regex.Match(name);
                    if (m.Success)
                    {
                        var com = m.Groups[1].Value; // COMx
                        Console.WriteLine($"PnP Fallback: {name} => {com}");
                        return com;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("PnP Fallback error: " + ex.Message);
            }

            // Letzter Notnagel: nimm den "höchsten" verfügbaren Port, wenn keine Hints passen
            var any = SerialPort.GetPortNames().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).LastOrDefault();
            if (!string.IsNullOrEmpty(any))
            {
                Console.WriteLine($"Last resort: using {any}");
                return any;
            }

            return null;
        }

        /// <summary>
        /// Primäres Protokoll: "x,y,click\n" (relative Bewegung).
        /// </summary>
        public void SendMouseCommand(int x, int y, int click)
        {
            if (!EnsureInitialized())
                return;

            if (_serialPort is null || !_serialPort.IsOpen)
            {
                _isInitialized = false; // wird beim nächsten EnsureInitialized() neu versucht
                Console.WriteLine("Send failed: port closed.");
                return;
            }

            try
            {
                string line = $"{x},{y},{click}\n";
                _serialPort.Write(line);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Write error: " + ex.Message);
                // Port als „defekt“ markieren, beim nächsten Aufruf neu initialisieren
                SafeClose();
                _isInitialized = false;
            }
        }

        // Optionales erweitertes Protokoll: separate Press/Release-Kommandos
        public void PressMouse()
        {
            if (!EnsureInitialized()) return;
            try { _serialPort!.Write("M_PRESS\n"); } catch { SafeClose(); _isInitialized = false; }
        }

        public void ReleaseMouse()
        {
            if (!EnsureInitialized()) return;
            try { _serialPort!.Write("M_RELEASE\n"); } catch { SafeClose(); _isInitialized = false; }
        }

        // Liefert den COM-Port-Namen (z. B. "COM4"), falls bekannt
        public string GetConnectedPort()
        {
            return _foundComPort ?? "unbekannt";
        }

        public void Close()
        {
            SafeClose();
            _foundComPort = null;
            _isInitialized = false;
        }


        private void SafeClose()
        {
            try { _serialPort?.Close(); } catch { /* ignore */ }
            try { _serialPort?.Dispose(); } catch { /* ignore */ }
            _serialPort = null;
        }
    }
}

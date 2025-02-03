using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using NAudio.Wave;
using NAudio.Dsp;
using ScottPlot;
using ScottPlot.Plottable;

namespace MorseCodeTranslator
{
    public partial class MainForm : Form
    {
        #region Audio Configuration
        private bool autoThreshold = true;
        private double noiseFloor = -60; // Minimum expected noise level in dB
        private CheckBox cbAutoThreshold;
        private WaveInEvent waveIn;
        private Complex[] fftBuffer;
        private int fftPos;
        private int targetFrequency = 600;
        private float threshold = 0.1f;
        private bool isRecording;
        private const int FFT_SIZE = 1024;
        private const int SAMPLE_RATE = 48000;
        #endregion

        #region Morse Code Configuration
        private MorseDecoder decoder;
        private SerialPort serialPort;
        private bool isTransmitting;
        #endregion

        #region UI Components
        private ComboBox cbAudioInput, cbComPorts;
        private NumericUpDown nudFrequency, nudSpeed;
        private TrackBar tbThreshold;
        private TextBox txtDecoded, txtMessage;
        private Button btnStartStop, btnSend, btnRefreshAudio, btnRefreshCom;
        private RadioButton rbDTR, rbRTS;
        private FormsPlot formsPlot;
        private VLine vLine;
        private Panel plotPanel;
        #endregion

        public MainForm()
        {
            InitializeUIComponents();
            InitializeAudio();
            InitializeMorse();
            InitializePlot();
            RefreshDevices();
        }

        #region Initialization
        private void InitializeUIComponents()
        {
            // Configure main form
            this.Text = "Morse Code Translator";
            this.Size = new Size(900, 700);
            this.Font = new Font("Segoe UI", 9f);
            this.FormClosing += (s, e) => CleanupResources();

            // Create audio input group
            var groupAudio = new GroupBox()
            {
                Text = "Audio Input",
                Location = new Point(10, 10),
                Size = new Size(430, 150)
            };

            cbAudioInput = new ComboBox { Location = new Point(10, 20), Size = new Size(300, 21) };
            btnRefreshAudio = new Button { Text = "Refresh", Location = new Point(320, 20), Size = new Size(100, 30) };
            btnRefreshAudio.Click += (s, e) => RefreshAudioDevices();

            btnStartStop = new Button
            {
                Text = "Start Listening",
                Location = new Point(10, 50),
                Size = new Size(410, 30),
                BackColor = Color.LimeGreen,
                ForeColor = Color.White
            };
            btnStartStop.Click += BtnStartStop_Click;

            nudFrequency = new NumericUpDown()
            {
                Minimum = 0,
                Maximum = 4000,
                Value = 600,
                Location = new Point(110, 90),
                Size = new Size(80, 20)
            };

            cbAutoThreshold = new CheckBox
            {
                Text = "Auto Threshold",
                Location = new Point(290, 120),
                Size = new Size(120, 20),
                Checked = true
            };
            cbAutoThreshold.CheckedChanged += (s, e) =>
            {
                autoThreshold = cbAutoThreshold.Checked;
                tbThreshold.Enabled = !autoThreshold;
            };
            groupAudio.Controls.Add(cbAutoThreshold);

            tbThreshold = new TrackBar()
            {
                Minimum = 1,
                Maximum = 100,
                Value = 10,
                Location = new Point(100, 120),
                Size = new Size(180, 20)
            };

            groupAudio.Controls.AddRange(new Control[] {
                cbAudioInput, btnRefreshAudio, btnStartStop,
                new Label { Text = "Frequency (Hz):", Location = new Point(10, 90) },
                nudFrequency,
                new Label { Text = "Threshold:", Location = new Point(10, 120) },
                tbThreshold
            });

            // Create COM port group
            var groupCom = new GroupBox()
            {
                Text = "COM Port",
                Location = new Point(450, 10),
                Size = new Size(430, 150)
            };

            cbComPorts = new ComboBox { Location = new Point(10, 20), Size = new Size(300, 21) };
            btnRefreshCom = new Button { Text = "Refresh", Location = new Point(320, 20), Size = new Size(100, 30) };
            btnRefreshCom.Click += (s, e) => RefreshComPorts();

            rbDTR = new RadioButton { Text = "DTR", Location = new Point(10, 90), Checked = true };
            rbRTS = new RadioButton { Text = "RTS", Location = new Point(120, 90) };

            nudSpeed = new NumericUpDown()
            {
                Minimum = 5,
                Maximum = 40,
                Value = 20,
                Location = new Point(120, 120),
                Size = new Size(80, 20)
            };

            groupCom.Controls.AddRange(new Control[] {
                cbComPorts, btnRefreshCom, rbDTR, rbRTS,
                new Label { Text = "Speed (WPM):", Location = new Point(10, 120) },
                nudSpeed
            });

            // Create signal display panel
            plotPanel = new Panel()
            {
                Location = new Point(10, 170),
                Size = new Size(870, 300),
                BackColor = Color.Black
            };

            // Create text displays
            txtDecoded = new TextBox()
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Location = new Point(10, 480),
                Size = new Size(870, 100)
            };

            txtMessage = new TextBox()
            {
                Location = new Point(10, 590),
                Size = new Size(700, 25)
            };

            btnSend = new Button()
            {
                Text = "Send",
                Location = new Point(720, 590),
                Size = new Size(160, 25)
            };
            btnSend.Click += BtnSend_Click;

            // Add all controls to form
            this.Controls.AddRange(new Control[] {
                groupAudio,
                groupCom,
                plotPanel,
                txtDecoded,
                txtMessage,
                btnSend
            });

            // Initialize plot
            formsPlot = new FormsPlot { Dock = DockStyle.Fill };
            plotPanel.Controls.Add(formsPlot);
        }

        private void InitializeAudio()
        {
            fftBuffer = new Complex[FFT_SIZE];
            waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(SAMPLE_RATE, 16, 1),
                BufferMilliseconds = 10
            };
            waveIn.DataAvailable += ProcessAudio;
            waveIn.RecordingStopped += (s, a) => SafeInvoke(() => UpdateRecordingState(false));
        }

        private void InitializeMorse()
        {
            decoder = new MorseDecoder();
            decoder.OnCharacterDecoded += (s, c) => SafeInvoke(() => txtDecoded.AppendText(c.ToString()));
        }

        private void InitializePlot()
        {
            formsPlot.Plot.Title("FFT Spectrum", size: 14);
            formsPlot.Plot.YLabel("Magnitude (dB)");
            formsPlot.Plot.XLabel("Frequency (Hz)");
            formsPlot.Plot.Style(ScottPlot.Style.Black);
            formsPlot.Plot.Grid(color: Color.FromArgb(40, 255, 255, 255));

            vLine = formsPlot.Plot.AddVerticalLine(targetFrequency, Color.Red, 1);
            vLine.DragEnabled = true;
            vLine.Dragged += (s, e) => UpdateTargetFrequency((int)vLine.X);
            formsPlot.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    UpdateTargetFrequency((int)formsPlot.GetMouseCoordinates().x);
            };
        }
        #endregion

        #region Audio Processing
        private void ProcessAudio(object sender, WaveInEventArgs e)
        {
            for (int i = 0; i < e.BytesRecorded; i += 2)
            {
                float sample = BitConverter.ToInt16(e.Buffer, i) / 32768f;

                if (fftPos < FFT_SIZE)
                {
                    double window = 0.54 - 0.46 * Math.Cos(2 * Math.PI * fftPos / FFT_SIZE);
                    fftBuffer[fftPos].X = (float)(sample * window);
                    fftBuffer[fftPos].Y = 0;
                    fftPos++;
                }

                if (fftPos >= FFT_SIZE)
                {
                    FastFourierTransform.FFT(true, (int)Math.Log(FFT_SIZE, 2), fftBuffer);
                    UpdateFFTDisplay();
                    DetectMorseTone();
                    fftPos = 0;
                }
            }
        }

        private void UpdateFFTDisplay()
        {
            var magnitudes = new double[FFT_SIZE / 2];
            var frequencies = new double[FFT_SIZE / 2];
            double freqStep = (double)SAMPLE_RATE / FFT_SIZE;

            for (int i = 0; i < magnitudes.Length; i++)
            {
                magnitudes[i] = 20 * Math.Log10(Math.Sqrt(
                    fftBuffer[i].X * fftBuffer[i].X +
                    fftBuffer[i].Y * fftBuffer[i].Y) / FFT_SIZE);
                frequencies[i] = i * freqStep;
            }

            SafeInvoke(() =>
            {
                // Auto-threshold calculation
                if (autoThreshold && magnitudes.Length > 0)
                {
                    // Calculate 95th percentile magnitude
                    var sorted = magnitudes.Where(m => m > noiseFloor).OrderBy(m => m).ToArray();
                    if (sorted.Length > 0)
                    {
                        double percentile95 = sorted[(int)(sorted.Length * 0.95)];
                        threshold = (float)((percentile95 - noiseFloor) / -noiseFloor); // Convert to 0-1 range
                        tbThreshold.Value = (int)(threshold * 100);
                    }
                }

                formsPlot.Plot.Clear();
                var sig = formsPlot.Plot.AddSignalXY(frequencies, magnitudes);
                sig.Color = Color.Cyan;
                sig.LineWidth = 1.5f;
                vLine.X = targetFrequency;
                formsPlot.Plot.SetAxisLimitsX(5, 3000);

                // Automatically adjust Y-axis based on current magnitudes
                if (magnitudes.Length > 0)
                {
                    double currentMax = magnitudes.Max();
                    double currentMin = magnitudes.Min();
                    double padding = 5.0; // 5dB padding

                    // Ensure valid range for dB scale
                    double yMax = Math.Min(currentMax + padding, 0); // Never exceed 0dB
                    double yMin = currentMin - padding;

                    // Ensure minimum range of 20dB for better visibility
                    if (yMax - yMin < 20)
                    {
                        yMin = yMax - 20;
                    }

                    formsPlot.Plot.SetAxisLimitsY(yMin, yMax);
                }
                else
                {
                    formsPlot.Plot.SetAxisLimitsY(-60, 0); // Fallback range
                }

                formsPlot.Refresh();
            });
        }
        #endregion

        #region Morse Code Handling
        private void DetectMorseTone()
        {
            int binIndex = (int)(targetFrequency * FFT_SIZE / SAMPLE_RATE);
            binIndex = Math.Clamp(binIndex, 0, FFT_SIZE / 2 - 1);

            float magnitude = (float)Math.Sqrt(
                fftBuffer[binIndex].X * fftBuffer[binIndex].X +
                fftBuffer[binIndex].Y * fftBuffer[binIndex].Y);

            // Convert threshold back to linear scale for comparison
            float thresholdLinear = (float)Math.Pow(10, (noiseFloor + (threshold * -noiseFloor)) / 20);
            decoder.ProcessSample(magnitude > thresholdLinear);
        }

        private void SendMorse(string text)
        {
            if (serialPort?.IsOpen != true || isTransmitting) return;

            isTransmitting = true;
            new Thread(() =>
            {
                var morse = MorseEncoder.Encode(text);
                foreach (var symbol in morse)
                {
                    if (!isTransmitting) break;

                    bool state = symbol != ' ';
                    SafeInvoke(() => SetSerialState(state));

                    int duration = MorseEncoder.GetSymbolDuration(symbol, (int)nudSpeed.Value);
                    Thread.Sleep(duration);
                }
                SafeInvoke(() => isTransmitting = false);
            }).Start();
        }
        #endregion

        #region UI Handlers
        private void BtnStartStop_Click(object sender, EventArgs e)
        {
            if (isRecording)
            {
                waveIn.StopRecording();
                UpdateRecordingState(false);
            }
            else
            {
                try
                {
                    waveIn.StartRecording();
                    UpdateRecordingState(true);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Audio Error: {ex.Message}");
                }
            }
        }

        private void BtnSend_Click(object sender, EventArgs e) => SendMorse(txtMessage.Text);

        private void UpdateRecordingState(bool recording)
        {
            isRecording = recording;
            btnStartStop.Text = recording ? "Stop Listening" : "Start Listening";
            btnStartStop.BackColor = recording ? Color.Red : Color.LimeGreen;
        }

        private void UpdateTargetFrequency(int frequency)
        {
            targetFrequency = Math.Clamp(frequency, 0, 2000);
            nudFrequency.Value = targetFrequency;
            vLine.X = targetFrequency;
            formsPlot.Refresh();
        }

        private void SetSerialState(bool state)
        {
            if (rbDTR.Checked) serialPort.DtrEnable = state;
            if (rbRTS.Checked) serialPort.RtsEnable = state;
        }
        #endregion

        #region Device Management
        private void RefreshDevices()
        {
            RefreshAudioDevices();
            RefreshComPorts();
        }

        private void RefreshAudioDevices()
        {
            cbAudioInput.Items.Clear();
            for (int i = 0; i < WaveIn.DeviceCount; i++)
                cbAudioInput.Items.Add(WaveIn.GetCapabilities(i).ProductName);
            cbAudioInput.SelectedIndex = WaveIn.DeviceCount > 0 ? 0 : -1;
        }

        private void RefreshComPorts()
        {
            cbComPorts.Items.Clear();
            cbComPorts.Items.AddRange(SerialPort.GetPortNames());
            cbComPorts.SelectedIndex = cbComPorts.Items.Count > 0 ? 0 : -1;
            UpdateSerialPort();
        }

        private void UpdateSerialPort()
        {
            try
            {
                serialPort?.Close();
                if (cbComPorts.SelectedItem == null) return;

                serialPort = new SerialPort(cbComPorts.SelectedItem.ToString());
                serialPort.Open();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"COM Port Error: {ex.Message}");
            }
        }
        #endregion

        #region Utilities
        private void SafeInvoke(Action action)
        {
            if (InvokeRequired) Invoke(action);
            else action();
        }

        private void InitializeComponent()
        {
            SuspendLayout();
            // 
            // MainForm
            // 
            ClientSize = new Size(933, 467);
            Name = "MainForm";
            ResumeLayout(false);
        }

        private void CleanupResources()
        {
            waveIn?.Dispose();
            serialPort?.Close();
        }
        #endregion
    }

    #region Morse Code Classes
    public class MorseDecoder
    {
        private DateTime lastChange = DateTime.Now;
        private bool lastState;
        private readonly List<char> buffer = new List<char>();

        public event EventHandler<char> OnCharacterDecoded;

        public void ProcessSample(bool state)
        {
            if (state == lastState) return;

            var duration = DateTime.Now - lastChange;
            AnalyzeDuration(duration, lastState);
            lastChange = DateTime.Now;
            lastState = state;
        }

        private void AnalyzeDuration(TimeSpan duration, bool wasTone)
        {
            if (wasTone)
                AddSymbol(duration.TotalMilliseconds);
            else
                CheckSpaces(duration.TotalMilliseconds);
        }

        private void AddSymbol(double ms) => buffer.Add(ms < 200 ? '.' : '-');

        private void CheckSpaces(double ms)
        {
            if (ms > 300 && buffer.Count > 0) DecodeCharacter();
            if (ms > 700) OnCharacterDecoded?.Invoke(this, ' ');
        }

        private void DecodeCharacter()
        {
            var code = string.Concat(buffer);
            var c = MorseCode.GetChar(code);
            if (c != null) OnCharacterDecoded?.Invoke(this, c.Value);
            buffer.Clear();
        }
    }

    public static class MorseEncoder
    {
        public static string Encode(string text) =>
            string.Join(" ", text.ToUpper().Select(c =>
                MorseCode.GetCode(c) ?? ""));

        public static int GetSymbolDuration(char symbol, int wpm) =>
            (symbol switch { '.' => 1, '-' => 3, _ => 3 }) * 1200 / wpm;
    }

    public static class MorseCode
    {
        public static readonly Dictionary<char, string> CodeMap = new()
        {
            {'A', ".-"}, {'B', "-..."}, {'C', "-.-."}, {'D', "-.."},
            {'E', "."}, {'F', "..-."}, {'G', "--."}, {'H', "...."},
            {'I', ".."}, {'J', ".---"}, {'K', "-.-"}, {'L', ".-.."},
            {'M', "--"}, {'N', "-."}, {'O', "---"}, {'P', ".--."},
            {'Q', "--.-"}, {'R', ".-."}, {'S', "..."}, {'T', "-"},
            {'U', "..-"}, {'V', "...-"}, {'W', ".--"}, {'X', "-..-"},
            {'Y', "-.--"}, {'Z', "--.."},
            {'0', "-----"}, {'1', ".----"}, {'2', "..---"}, {'3', "...--"},
            {'4', "....-"}, {'5', "....."}, {'6', "-...."}, {'7', "--..."},
            {'8', "---.."}, {'9', "----."},
            {'.', ".-.-.-"}, {',', "--..--"}, {'?', "..--.."}, {'\'', ".----."},
            {'!', "-.-.--"}, {'/', "-..-."}, {'(', "-.--."}, {')', "-.--.-"},
            {'&', ".-..."}, {':', "---..."}, {';', "-.-.-."}, {'=', "-...-"},
            {'+', ".-.-."}, {'-', "-....-"}, {'_', "..--.-"}, {'"', ".-..-."},
            {'$', "...-..-"}, {'@', ".--.-."}, {' ', "/"}
        };

        public static string GetCode(char c) =>
            CodeMap.TryGetValue(char.ToUpper(c), out var code) ? code : null;

        public static char? GetChar(string code) =>
            CodeMap.FirstOrDefault(x => x.Value == code).Key;
    }
    #endregion
}
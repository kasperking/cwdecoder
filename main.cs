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
using ScottPlot.WinForms;

namespace MorseCodeTranslator
{
    public partial class MainForm : Form
    {
        // Audio Processing
        private WaveInEvent waveIn;
        private Complex[] fftBuffer;
        private int fftPos;
        private int targetFrequency = 600;
        private float threshold = 0.1f;
        private bool isRecording;

        // Morse Code Handling
        private MorseDecoder decoder;
        private SerialPort serialPort;
        private bool isTransmitting;

        // UI Components
        private GroupBox groupAudio, groupCom;
        private ComboBox cbAudioInput, cbComPorts;
        private NumericUpDown nudFrequency, nudSpeed;
        private TrackBar tbThreshold;
        private TextBox txtDecoded, txtMessage;
        private Button btnStartStop, btnSend, btnRefreshAudio, btnRefreshCom;
        private RadioButton rbDTR, rbRTS;
        private FormsPlot formsPlot;
        private Panel plotPanel;

        public MainForm()
        {
            InitializeComponent();
            InitializeAudio();
            InitializeMorse();
            InitializePlot();
        }

        private void InitializeComponent()
        {
            // Form Setup
            this.Text = "Morse Code Translator";
            this.Size = new Size(800, 650);
            this.Font = new Font("Segoe UI", 9f);
            this.FormClosing += (s, e) => CleanupResources();

            // Audio Input Group
            groupAudio = new GroupBox()
            {
                Text = "Audio Input",
                Location = new Point(10, 10),
                Size = new Size(380, 150)
            };

            cbAudioInput = new ComboBox { Location = new Point(10, 20), Size = new Size(240, 21) };
            btnRefreshAudio = new Button { Text = "Refresh", Location = new Point(260, 20), Size = new Size(100, 23) };
            btnRefreshAudio.Click += (s, e) => RefreshAudioDevices();

            btnStartStop = new Button
            {
                Text = "Start Listening",
                Location = new Point(10, 50),
                Size = new Size(350, 30),
                BackColor = Color.LimeGreen,
                ForeColor = Color.White
            };
            btnStartStop.Click += BtnStartStop_Click;

            nudFrequency = new NumericUpDown()
            {
                Minimum = 300,
                Maximum = 3000,
                Value = 600,
                Location = new Point(120, 90),
                Size = new Size(80, 20)
            };

            tbThreshold = new TrackBar()
            {
                Minimum = 1,
                Maximum = 100,
                Value = 10,
                Location = new Point(100, 120),
                Size = new Size(180, 20)
            };

            groupAudio.Controls.AddRange(new Control[] {
                cbAudioInput,
                btnRefreshAudio,
                btnStartStop,
                new Label { Text = "Frequency (Hz):", Location = new Point(10, 90) },
                nudFrequency,
                new Label { Text = "Threshold:", Location = new Point(10, 120) },
                tbThreshold
            });

            // COM Port Group
            groupCom = new GroupBox()
            {
                Text = "COM Port",
                Location = new Point(400, 10),
                Size = new Size(380, 150)
            };

            cbComPorts = new ComboBox { Location = new Point(10, 20), Size = new Size(240, 21) };
            btnRefreshCom = new Button { Text = "Refresh", Location = new Point(260, 20), Size = new Size(100, 23) };
            btnRefreshCom.Click += (s, e) => RefreshComPorts();

            rbDTR = new RadioButton { Text = "DTR", Location = new Point(10, 90), Checked = true };
            rbRTS = new RadioButton { Text = "RTS", Location = new Point(120, 90) };

            nudSpeed = new NumericUpDown()
            {
                Minimum = 5,
                Maximum = 40,
                Value = 20,
                Location = new Point(140, 120),
                Size = new Size(80, 20)
            };

            groupCom.Controls.AddRange(new Control[] {
                cbComPorts,
                btnRefreshCom,
                rbDTR,
                rbRTS,
                new Label { Text = "Speed (WPM):", Location = new Point(10, 120) },
                nudSpeed
            });

            // Signal Display
            plotPanel = new Panel()
            {
                Location = new Point(10, 170),
                Size = new Size(770, 200),
                BackColor = Color.Black
            };

            // Text Displays
            txtDecoded = new TextBox()
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Location = new Point(10, 380),
                Size = new Size(770, 100)
            };

            txtMessage = new TextBox()
            {
                Location = new Point(10, 490),
                Size = new Size(600, 25)
            };

            btnSend = new Button()
            {
                Text = "Send",
                Location = new Point(620, 490),
                Size = new Size(160, 25)
            };

            this.Controls.AddRange(new Control[] {
                groupAudio,
                groupCom,
                plotPanel,
                txtDecoded,
                txtMessage,
                btnSend
            });

            // Event Handlers
            btnSend.Click += BtnSend_Click;
            nudFrequency.ValueChanged += (s, e) => targetFrequency = (int)nudFrequency.Value;
            tbThreshold.Scroll += (s, e) => threshold = tbThreshold.Value / 100f;
            cbComPorts.SelectedIndexChanged += (s, e) => UpdateSerialPort();
        }

        private void InitializeAudio()
        {
            waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(8000, 16, 1),
                BufferMilliseconds = 50,
                DeviceNumber = 0
            };

            waveIn.DataAvailable += ProcessAudio;
            waveIn.RecordingStopped += (s, a) => SafeInvoke(() =>
            {
                isRecording = false;
                btnStartStop.Text = "Start Listening";
                btnStartStop.BackColor = Color.LimeGreen;
            });

            fftBuffer = new Complex[1024];
            RefreshAudioDevices();
        }

        private void InitializeMorse()
        {
            decoder = new MorseDecoder();
            decoder.OnCharacterDecoded += (s, c) => SafeInvoke(() => txtDecoded.AppendText(c.ToString()));
            RefreshComPorts();
        }

        private void InitializePlot()
        {
            formsPlot = new FormsPlot { Dock = DockStyle.Fill };
            plotPanel.Controls.Add(formsPlot);

            formsPlot.Plot.Title("FFT Spectrum", size: 14);
            formsPlot.Plot.XLabel("Frequency (Hz)");
            formsPlot.Plot.YLabel("Magnitude");
            formsPlot.Plot.Style(ScottPlot.Style.Black);
            formsPlot.Plot.Grid(color: Color.FromArgb(40, 255, 255, 255));
        }

        private void ProcessAudio(object sender, WaveInEventArgs e)
        {
            for (int i = 0; i < e.BytesRecorded; i += 2)
            {
                float sample = BitConverter.ToInt16(e.Buffer, i) / 32768f;
                fftBuffer[fftPos].X = sample;
                fftBuffer[fftPos].Y = 0;
                fftPos++;

                if (fftPos >= fftBuffer.Length)
                {
                    FastFourierTransform.FFT(true, (int)Math.Log(fftBuffer.Length, 2), fftBuffer);
                    UpdateFFTDisplay();
                    DetectMorseTone();
                    fftPos = 0;
                }
            }
        }

        private void UpdateFFTDisplay()
        {
            if (waveIn?.WaveFormat == null) return;

            var sampleRate = waveIn.WaveFormat.SampleRate;
            var fftSize = fftBuffer.Length;
            var pointCount = fftSize / 2;

            var freqStep = (double)sampleRate / fftSize;
            var frequencies = Enumerable.Range(0, pointCount)
                                       .Select(i => i * freqStep)
                                       .ToArray();

            var magnitudes = new double[pointCount];
            for (int i = 0; i < pointCount; i++)
            {
                magnitudes[i] = Math.Sqrt(fftBuffer[i].X * fftBuffer[i].X +
                                        fftBuffer[i].Y * fftBuffer[i].Y);
            }

            SafeInvoke(() =>
            {
                formsPlot.Plot.Clear();
                var scatter = formsPlot.Plot.AddScatter(frequencies, magnitudes);
                scatter.Color = Color.Cyan;
                scatter.LineWidth = 1;

                var vLine = formsPlot.Plot.AddVerticalLine(targetFrequency, Color.Red, 1);
                vLine.PositionLabel = true;
                vLine.Label = $"{targetFrequency} Hz";

                formsPlot.Plot.SetAxisLimitsX(0, targetFrequency * 2);
                formsPlot.Plot.SetAxisLimitsY(0, magnitudes.Max() * 1.2);
                formsPlot.Refresh();
            });
        }

        private void DetectMorseTone()
        {
            int binIndex = (int)(targetFrequency * fftBuffer.Length / waveIn.WaveFormat.SampleRate);
            float magnitude = (float)Math.Sqrt(
                fftBuffer[binIndex].X * fftBuffer[binIndex].X +
                fftBuffer[binIndex].Y * fftBuffer[binIndex].Y);

            decoder.ProcessSample(magnitude > threshold);
        }

        private void BtnStartStop_Click(object sender, EventArgs e)
        {
            if (isRecording)
            {
                waveIn.StopRecording();
                isRecording = false;
                btnStartStop.Text = "Start Listening";
                btnStartStop.BackColor = Color.LimeGreen;
            }
            else
            {
                try
                {
                    waveIn.StartRecording();
                    isRecording = true;
                    btnStartStop.Text = "Stop Listening (●)";
                    btnStartStop.BackColor = Color.Red;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Audio Error: {ex.Message}");
                    isRecording = false;
                }
            }
        }

        private void BtnSend_Click(object sender, EventArgs e)
        {
            if (serialPort?.IsOpen != true || isTransmitting) return;

            isTransmitting = true;
            new Thread(() =>
            {
                var morse = MorseEncoder.Encode(txtMessage.Text);
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

        private void SetSerialState(bool state)
        {
            if (rbDTR.Checked) serialPort.DtrEnable = state;
            if (rbRTS.Checked) serialPort.RtsEnable = state;
        }

        private void RefreshAudioDevices()
        {
            cbAudioInput.Items.Clear();
            for (int i = 0; i < WaveIn.DeviceCount; i++)
                cbAudioInput.Items.Add(WaveIn.GetCapabilities(i).ProductName);
            if (cbAudioInput.Items.Count > 0) cbAudioInput.SelectedIndex = 0;
        }

        private void RefreshComPorts()
        {
            cbComPorts.Items.Clear();
            cbComPorts.Items.AddRange(SerialPort.GetPortNames());
            if (cbComPorts.Items.Count > 0) cbComPorts.SelectedIndex = 0;
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

        private void SafeInvoke(Action action)
        {
            if (InvokeRequired) Invoke(action);
            else action();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            CleanupResources();
            base.OnFormClosing(e);
        }

        private void CleanupResources()
        {
            waveIn?.StopRecording();
            waveIn?.Dispose();
            serialPort?.Close();
        }

        
    }

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
}
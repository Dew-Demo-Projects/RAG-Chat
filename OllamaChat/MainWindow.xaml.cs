using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using MdXaml;
using OllamaLibrary;

namespace chat
{
    public partial class MainWindow : Window
    {
        private OllamaLibrary.OllamaClient _ollamaChatClient;
        private OllamaEmbedding _ollamaEmbedding;
        private RagDatabaseService _ragService;

        private DispatcherTimer _statusTimer;
        private string _statusBaseText = string.Empty;
        private int _statusDotCount;

        private readonly Markdown _markdown = new Markdown();

        private string? _openThoughtId;


        // Thinking storage
        private readonly Dictionary<string, string> _thoughtById = new();
        private string? _activeThoughtId;
        private bool _thinkingSeenThisTurn;

        // Context list stays as in your current app
        private List<RagChunk> _currentContext = new();

        private readonly ObservableCollection<ChatMessageVm> _messages = new();

        public MainWindow()
        {
            InitializeComponent();

            ChatList.ItemsSource = _messages;

            InitializeServices();

            AddSystemMessage("Aplikacja gotowa do pytań genealogicznych!");
        }

        private void InitializeServices()
        {
            try
            {
                var connectionString =
                    "Host=localhost;Port=5432;Database=genealogy;Username=admin;Password=A232cb1";

                _ollamaChatClient = new OllamaLibrary.OllamaClient(
                    "deepseek-r1:32b",
                    "http://192.168.107.37:11434",
                    "ministral-3:3b");

                _ollamaEmbedding = new OllamaEmbedding("qwen3-embedding:8b", "http://192.168.107.37:11434");
                _ragService = new RagDatabaseService(connectionString, _ollamaEmbedding);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Błąd inicjalizacji: {ex.Message}",
                    "Błąd",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void User_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !Keyboard.IsKeyDown(Key.LeftShift))
            {
                e.Handled = true;
                await ProcessQuestion();
            }
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            await ProcessQuestion();
        }

        private async Task ProcessQuestion()
        {
            var question = User.Text.Trim();
            if (string.IsNullOrWhiteSpace(question))
                return;

            User.Clear();
            User.IsEnabled = false;

            // Reset per-turn state
            _activeThoughtId = null;
            _thinkingSeenThisTurn = false;

            // Add user message
            AddUserMessage(question);

            // Add assistant placeholder message (we will update it as tokens arrive)
            var assistantMsg = AddAssistantMessage("...");

            var thinkingBuilder = new StringBuilder();
            var answerBuilder = new StringBuilder();
            bool answerStarted = false;

            try
            {
                var (answerStream, context) =
                    await _ollamaChatClient.AnswerWithOptionalContextStreamAsync(
                        question,
                        _ragService.SearchSimilarChunksAsync,
                        _ragService.ExpandContextAsync,
                        status => Dispatcher.InvokeAsync(() => StartStatus(status)),
                        cancellationToken: default
                    );

                _currentContext = context;
                ContextList.ItemsSource = _currentContext;

                await Task.Run(async () =>
                {
                    await foreach (var (token, isThinking) in answerStream)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (ThinkingOverlay.Visibility == Visibility.Visible &&
                                _openThoughtId == _activeThoughtId)
                            {
                                if (_activeThoughtId != null) ThinkingText.Text = _thoughtById[_activeThoughtId];
                                ThinkingText.ScrollToEnd();
                            }

                            if (isThinking)
                            {
                                if (!_thinkingSeenThisTurn)
                                {
                                    _thinkingSeenThisTurn = true;
                                    StartStatus("🧠 Model myśli");
                                }

                                _activeThoughtId ??= Guid.NewGuid().ToString("N");

                                thinkingBuilder.Append(token);
                                _thoughtById[_activeThoughtId] = thinkingBuilder.ToString();

                                // Make the button appear as soon as thinking starts
                                assistantMsg.ThoughtId = _activeThoughtId;
                                assistantMsg.ShowThinkingButton = Visibility.Visible;

                                return;
                            }

                            if (!answerStarted)
                            {
                                answerStarted = true;
                                StopStatus();
                                answerBuilder.Clear();
                            }

                            answerBuilder.Append(token);

                            assistantMsg.SetMarkdown(answerBuilder.ToString(), _markdown,
                                TryFindResource("DarkMarkdownStyle") as Style);
                            ScrollChatToBottom();
                        });
                    }
                });

                StopStatus();

                if (!answerStarted)
                {
                    // Model never produced final answer tokens
                    var msg = _activeThoughtId is null
                        ? "_Brak odpowiedzi._"
                        : "_Brak treści odpowiedzi (model zakończył na myśleniu)._";

                    assistantMsg.SetMarkdown(msg, _markdown, TryFindResource("DarkMarkdownStyle") as Style);
                }
            }
            catch (Exception ex)
            {
                StopStatus();
                assistantMsg.SetMarkdown($"❌ **Błąd:** {ex.Message}", _markdown,
                    TryFindResource("DarkMarkdownStyle") as Style);
            }
            finally
            {
                User.IsEnabled = true;
                User.Focus();
                ScrollChatToBottom();
            }
        }

        // Standard WPF click handler (no hyperlink weirdness)
        private void ShowThinking_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            if (btn.Tag is not string thoughtId) return;

            _openThoughtId = thoughtId;

            ThinkingText.Text = _thoughtById.GetValueOrDefault(thoughtId, "");

            ThinkingOverlay.Visibility = Visibility.Visible;
            ThinkingText.ScrollToEnd();
        }

        private void CloseThinking_Click(object sender, RoutedEventArgs e)
        {
            ThinkingOverlay.Visibility = Visibility.Collapsed;
            _openThoughtId = null;
        }


        private void ContextList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ContextList.SelectedItem is RagChunk chunk)
                ShowDetailsForChunk(chunk);
        }

        private void ShowDetailsForChunk(RagChunk chunk)
        {
            var text = chunk.Content ?? string.Empty;

            if (chunk.Metadata != null &&
                chunk.Metadata.TryGetValue("source_type", out var t) &&
                chunk.Metadata.TryGetValue("source_id", out var id))
            {
                text = $"[{t} {id}]\n\n" + text;
            }

            Details.Text = text;
        }

        private void StartStatus(string baseText)
        {
            StopStatus();

            _statusBaseText = baseText;
            _statusDotCount = 0;

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _statusTimer.Tick += (_, _) =>
            {
                _statusDotCount = (_statusDotCount + 1) % 4;
                var dots = new string('.', _statusDotCount);
                RunStatusText.Text = $" • {_statusBaseText}{dots}";
            };
            _statusTimer.Start();
        }

        private void StopStatus()
        {
            _statusTimer?.Stop();
            _statusTimer = null;

            if (RunStatusText != null)
                RunStatusText.Text = " • Gotowy";
        }

        private void ScrollChatToBottom()
        {
            if (_messages.Count > 0)
                ChatList.ScrollIntoView(_messages[_messages.Count - 1]);
        }

        private void AddSystemMessage(string text)
        {
            var vm = new ChatMessageVm("System");
            vm.SetMarkdown($"{text}", _markdown, TryFindResource("DarkMarkdownStyle") as Style);
            _messages.Add(vm);
        }

        private void AddUserMessage(string text)
        {
            var vm = new ChatMessageVm("Ty");
            vm.SetMarkdown($"{text}", _markdown, TryFindResource("DarkMarkdownStyle") as Style);
            _messages.Add(vm);
            ScrollChatToBottom();
        }

        private ChatMessageVm AddAssistantMessage(string text)
        {
            var vm = new ChatMessageVm("Asystent");
            vm.SetMarkdown($"{text}", _markdown, TryFindResource("DarkMarkdownStyle") as Style);
            _messages.Add(vm);
            ScrollChatToBottom();
            return vm;
        }

        protected override void OnClosed(EventArgs e)
        {
            _ragService?.Dispose();
            _ollamaChatClient?.Dispose();
            base.OnClosed(e);
        }
    }

    public class ChatMessageVm : INotifyPropertyChanged
    {
        public string Header { get; }

        private FlowDocument _document = new FlowDocument();

        public FlowDocument Document
        {
            get => _document;
            private set
            {
                if (ReferenceEquals(_document, value)) return;
                _document = value;
                OnPropertyChanged();
            }
        }

        private string? _thoughtId;

        public string? ThoughtId
        {
            get => _thoughtId;
            set
            {
                if (_thoughtId == value) return;
                _thoughtId = value;
                OnPropertyChanged();
            }
        }

        private Visibility _showThinkingButton = Visibility.Collapsed;

        public Visibility ShowThinkingButton
        {
            get => _showThinkingButton;
            set
            {
                if (_showThinkingButton == value) return;
                _showThinkingButton = value;
                OnPropertyChanged();
            }
        }

        public ChatMessageVm(string header)
        {
            Header = header + ":";
        }

        public void SetMarkdown(string markdown, Markdown renderer, Style? docStyle)
        {
            var doc = renderer.Transform(markdown);

            if (docStyle != null)
                doc.Style = docStyle;

            doc.Background = null; // keep panel background from your Border

            Document = doc; // IMPORTANT: uses setter -> notifies UI
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
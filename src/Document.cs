using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Threading;
using Tomlyn;
using Tomlyn.Syntax;

namespace TomlEditor
{
    public class Document : IDisposable
    {
        private static readonly TimeSpan ParseDelay = TimeSpan.FromMilliseconds(300);

        private readonly ITextBuffer _buffer;
        private readonly object _parseLock = new();
        private CancellationTokenSource _parseCts;
        private bool _isDisposed;
        private volatile bool _isParsing;

        public Document(ITextBuffer buffer)
        {
            _buffer = buffer;
            _buffer.Changed += OnBufferChanged;

            FileName = _buffer.GetFileName();
            RequestParse();
        }

        public string FileName { get; }

        public bool IsParsing => _isParsing;

        public DocumentSyntax Model { get; private set; }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            RequestParse();
        }

        private void RequestParse()
        {
            lock (_parseLock)
            {
                _parseCts?.Cancel();
                _parseCts = new CancellationTokenSource();
                ParseAsync(_parseCts.Token).FireAndForget();
            }
        }

        private async Task ParseAsync(CancellationToken cancellationToken)
        {
            _isParsing = true;
            var success = false;

            try
            {
                await Task.Delay(ParseDelay, cancellationToken);
                await TaskScheduler.Default; // move to a background thread

                cancellationToken.ThrowIfCancellationRequested();

                var text = _buffer.CurrentSnapshot.GetText();
                DocumentSyntax model = Toml.Parse(text, FileName, TomlParserOptions.ParseAndValidate);

                cancellationToken.ThrowIfCancellationRequested();

                Model = model;
                success = true;
            }
            catch (OperationCanceledException)
            {
                // Parse was cancelled due to newer changes; ignore
            }
            finally
            {
                _isParsing = false;

                if (success)
                {
                    Action<Document> handler = Parsed;
                    handler?.Invoke(this);
                }
            }
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _buffer.Changed -= OnBufferChanged;
                _parseCts?.Cancel();
                _parseCts?.Dispose();
                Model = null;
            }

            _isDisposed = true;
        }

        public event Action<Document> Parsed;
    }
}

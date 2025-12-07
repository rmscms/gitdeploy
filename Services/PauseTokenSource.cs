using System.Threading;
using System.Threading.Tasks;

namespace GitDeployPro.Services
{
    public sealed class PauseTokenSource
    {
        private volatile bool _isPaused;

        public bool IsPaused => _isPaused;

        public void Pause() => _isPaused = true;

        public void Resume() => _isPaused = false;

        public async Task WaitWhilePausedAsync(CancellationToken token)
        {
            if (!_isPaused)
            {
                return;
            }

            while (_isPaused)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(150, token).ConfigureAwait(false);
            }
        }
    }
}


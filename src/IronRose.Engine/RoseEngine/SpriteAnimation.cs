namespace RoseEngine
{
    /// <summary>
    /// 스프라이트 프레임 애니메이션 컴포넌트.
    /// Sprite 배열을 순서대로 전환하여 프레임 기반 애니메이션 재생.
    /// </summary>
    [RequireComponent(typeof(SpriteRenderer))]
    public class SpriteAnimation : MonoBehaviour
    {
        public Sprite[]? frames;
        public float framesPerSecond = 12f;
        public bool loop = true;

        private SpriteRenderer? _renderer;
        private float _timer;
        private int _currentFrame;
        private bool _isPlaying;

        public bool isPlaying => _isPlaying;
        public int currentFrame => _currentFrame;

        public override void Start()
        {
            _renderer = GetComponent<SpriteRenderer>();
            if (frames != null && frames.Length > 0)
                Play();
        }

        public void Play()
        {
            _timer = 0f;
            _currentFrame = 0;
            _isPlaying = true;
            ApplyFrame();
        }

        public void Stop()
        {
            _isPlaying = false;
        }

        public override void Update()
        {
            if (!_isPlaying || frames == null || frames.Length == 0 || _renderer == null)
                return;

            if (framesPerSecond <= 0f) return;

            _timer += Time.deltaTime;
            float frameDuration = 1f / framesPerSecond;

            while (_timer >= frameDuration)
            {
                _timer -= frameDuration;
                _currentFrame++;

                if (_currentFrame >= frames.Length)
                {
                    if (loop)
                    {
                        _currentFrame = 0;
                    }
                    else
                    {
                        _currentFrame = frames.Length - 1;
                        _isPlaying = false;
                        break;
                    }
                }
            }

            ApplyFrame();
        }

        private void ApplyFrame()
        {
            if (_renderer != null && frames != null && _currentFrame < frames.Length)
                _renderer.sprite = frames[_currentFrame];
        }
    }
}

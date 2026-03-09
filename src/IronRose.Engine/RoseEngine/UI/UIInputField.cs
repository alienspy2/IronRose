using System;
using ImGuiNET;
using SNVector2 = System.Numerics.Vector2;

namespace RoseEngine
{
    public enum InputFieldContentType
    {
        Standard,
        IntegerNumber,
        DecimalNumber,
        Alphanumeric,
        Password
    }

    public class UIInputField : Component, IUIRenderable
    {
        public Font? font;
        public string text = "";
        public string placeholder = "";
        public float fontSize = 16f;
        public int maxLength = 256;
        public InputFieldContentType contentType = InputFieldContentType.Standard;

        public Color textColor = Color.white;
        public Color placeholderColor = new(0.5f, 0.5f, 0.5f, 0.6f);
        public Color backgroundColor = new(0.15f, 0.15f, 0.15f, 1f);
        public Color focusedBorderColor = new(0.3f, 0.6f, 0.9f, 1f);
        public Color borderColor = new(0.3f, 0.3f, 0.3f, 1f);
        public Color selectionColor = new(0.2f, 0.4f, 0.7f, 0.5f);
        public float padding = 4f;

        public bool interactable = true;
        public bool readOnly;

        public Action<string>? onValueChanged;
        public Action<string>? onEndEdit;

        // Internal editing state
        private bool _isFocused;
        private int _cursorPos;
        private int _selectionStart = -1; // -1 = no selection
        private float _cursorBlinkTimer;
        private const float CursorBlinkInterval = 0.53f;

        // Global focus tracking (only one UIInputField can be focused at a time)
        private static UIInputField? _currentFocused;

        internal static readonly List<UIInputField> _allUIInputFields = new();

        internal override void OnAddedToGameObject() => _allUIInputFields.Add(this);
        internal override void OnComponentDestroy() => _allUIInputFields.Remove(this);
        internal static void ClearAll() => _allUIInputFields.Clear();

        public int renderOrder => 5;

        public void OnRenderUI(ImDrawListPtr drawList, Rect screenRect)
        {
            // ── Background ──
            uint bgCol = ColorToU32(backgroundColor);
            uint borderCol = ColorToU32(_isFocused ? focusedBorderColor : borderColor);
            drawList.AddRectFilled(
                new SNVector2(screenRect.x, screenRect.y),
                new SNVector2(screenRect.xMax, screenRect.yMax),
                bgCol, 2f);
            drawList.AddRect(
                new SNVector2(screenRect.x, screenRect.y),
                new SNVector2(screenRect.xMax, screenRect.yMax),
                borderCol, 2f);

            // No font → draw background only, no text
            if (font == null || font.atlasTexture == null) return;

            float scale = fontSize / font.fontSize;

            // ── Hit test / focus (Play Mode에서만 입력 모드 진입) ──
            if (interactable && Application.isPlaying)
            {
                var mousePos = ImGui.GetMousePos();
                bool inRect = mousePos.X >= screenRect.x && mousePos.X <= screenRect.xMax &&
                              mousePos.Y >= screenRect.y && mousePos.Y <= screenRect.yMax;

                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    if (inRect)
                    {
                        if (!_isFocused)
                        {
                            Focus();
                            _cursorPos = GetCharIndexAtX(text, screenRect.x + padding, mousePos.X, scale);
                            _selectionStart = -1;
                        }
                        else
                        {
                            _cursorPos = GetCharIndexAtX(text, screenRect.x + padding, mousePos.X, scale);
                            _selectionStart = -1;
                        }
                    }
                    else if (_isFocused)
                    {
                        Unfocus();
                    }
                }
            }

            // ── Clip content to field area ──
            float contentX = screenRect.x + padding;
            float contentY = screenRect.y + (screenRect.height - fontSize) * 0.5f;
            drawList.PushClipRect(
                new SNVector2(screenRect.x + 1, screenRect.y + 1),
                new SNVector2(screenRect.xMax - 1, screenRect.yMax - 1), true);

            // ── Process keyboard input when focused ──
            if (_isFocused)
            {
                ProcessKeyboardInput();
                _cursorBlinkTimer += Time.unscaledDeltaTime;
            }

            // ── Draw text or placeholder ──
            string displayText = contentType == InputFieldContentType.Password
                ? new string('*', text.Length)
                : text;

            var texId = CanvasRenderer.GetTextureId(font.atlasTexture);

            if (string.IsNullOrEmpty(displayText) && !_isFocused)
            {
                // Placeholder
                if (texId != IntPtr.Zero)
                    DrawGlyphText(drawList, texId, font, placeholder, scale,
                        contentX, contentY, ColorToU32(placeholderColor));
            }
            else
            {
                // Selection highlight
                if (_isFocused && HasSelection())
                {
                    int selMin = Math.Min(_selectionStart, _cursorPos);
                    int selMax = Math.Max(_selectionStart, _cursorPos);
                    float selStartX = contentX + MeasureText(displayText, 0, selMin, scale);
                    float selEndX = contentX + MeasureText(displayText, 0, selMax, scale);
                    drawList.AddRectFilled(
                        new SNVector2(selStartX, screenRect.y + 2),
                        new SNVector2(selEndX, screenRect.yMax - 2),
                        ColorToU32(selectionColor));
                }

                // Text
                if (texId != IntPtr.Zero)
                    DrawGlyphText(drawList, texId, font, displayText, scale,
                        contentX, contentY, ColorToU32(textColor));
            }

            // ── Cursor ──
            if (_isFocused && !readOnly)
            {
                bool showCursor = ((int)(_cursorBlinkTimer / CursorBlinkInterval)) % 2 == 0;
                if (showCursor)
                {
                    float cursorX = contentX + MeasureText(displayText, 0, _cursorPos, scale);
                    drawList.AddLine(
                        new SNVector2(cursorX, screenRect.y + 3),
                        new SNVector2(cursorX, screenRect.yMax - 3),
                        ColorToU32(textColor), 1f);
                }
            }

            drawList.PopClipRect();
        }

        // ── Glyph Rendering ──

        private static void DrawGlyphText(ImDrawListPtr drawList, IntPtr texId, Font font,
            string str, float scale, float x, float y, uint col)
        {
            if (string.IsNullOrEmpty(str)) return;

            float cursorX = x;
            foreach (char ch in str)
            {
                if (!font.glyphs.TryGetValue(ch, out var g))
                {
                    cursorX += font.fontSize * 0.5f * scale;
                    continue;
                }

                float w = g.width * scale;
                float h = g.height * scale;

                drawList.AddImage(texId,
                    new SNVector2(cursorX, y),
                    new SNVector2(cursorX + w, y + h),
                    new SNVector2(g.uvMin.x, g.uvMin.y),
                    new SNVector2(g.uvMax.x, g.uvMax.y),
                    col);

                cursorX += g.advance * scale;
            }
        }

        // ── Keyboard Input Processing ──

        private void ProcessKeyboardInput()
        {
            if (readOnly && !IsCtrlHeld()) return;

            bool ctrl = IsCtrlHeld();
            bool shift = IsShiftHeld();

            // Character input
            if (!readOnly)
            {
                foreach (char c in Input.inputString)
                {
                    if (c < 32) continue;
                    if (!IsCharAllowed(c)) continue;
                    if (text.Length >= maxLength && !HasSelection()) continue;

                    DeleteSelection();
                    text = text.Insert(_cursorPos, c.ToString());
                    _cursorPos++;
                    _selectionStart = -1;
                    _cursorBlinkTimer = 0;
                    NotifyValueChanged();
                }
            }

            // Ctrl+A — Select All
            if (ctrl && Input.GetKeyDown(KeyCode.A))
            {
                _selectionStart = 0;
                _cursorPos = text.Length;
                return;
            }

            // Ctrl+C — Copy
            if (ctrl && Input.GetKeyDown(KeyCode.C))
            {
                CopyToClipboard();
                return;
            }

            // Ctrl+X — Cut
            if (ctrl && Input.GetKeyDown(KeyCode.X))
            {
                if (!readOnly)
                {
                    CopyToClipboard();
                    DeleteSelection();
                    NotifyValueChanged();
                }
                return;
            }

            // Ctrl+V — Paste
            if (ctrl && Input.GetKeyDown(KeyCode.V))
            {
                if (!readOnly)
                    PasteFromClipboard();
                return;
            }

            if (readOnly) return;

            // Backspace
            if (Input.GetKeyDown(KeyCode.Backspace))
            {
                if (HasSelection())
                {
                    DeleteSelection();
                }
                else if (_cursorPos > 0)
                {
                    text = text.Remove(_cursorPos - 1, 1);
                    _cursorPos--;
                }
                _selectionStart = -1;
                _cursorBlinkTimer = 0;
                NotifyValueChanged();
            }

            // Delete
            if (Input.GetKeyDown(KeyCode.Delete))
            {
                if (HasSelection())
                {
                    DeleteSelection();
                }
                else if (_cursorPos < text.Length)
                {
                    text = text.Remove(_cursorPos, 1);
                }
                _selectionStart = -1;
                _cursorBlinkTimer = 0;
                NotifyValueChanged();
            }

            // Left Arrow
            if (Input.GetKeyDown(KeyCode.LeftArrow))
            {
                if (shift)
                {
                    if (_selectionStart < 0) _selectionStart = _cursorPos;
                }
                else if (HasSelection())
                {
                    _cursorPos = Math.Min(_selectionStart, _cursorPos);
                    _selectionStart = -1;
                    return;
                }
                else
                {
                    _selectionStart = -1;
                }
                if (_cursorPos > 0) _cursorPos--;
                _cursorBlinkTimer = 0;
            }

            // Right Arrow
            if (Input.GetKeyDown(KeyCode.RightArrow))
            {
                if (shift)
                {
                    if (_selectionStart < 0) _selectionStart = _cursorPos;
                }
                else if (HasSelection())
                {
                    _cursorPos = Math.Max(_selectionStart, _cursorPos);
                    _selectionStart = -1;
                    return;
                }
                else
                {
                    _selectionStart = -1;
                }
                if (_cursorPos < text.Length) _cursorPos++;
                _cursorBlinkTimer = 0;
            }

            // Home
            if (Input.GetKeyDown(KeyCode.Home))
            {
                if (shift && _selectionStart < 0) _selectionStart = _cursorPos;
                else if (!shift) _selectionStart = -1;
                _cursorPos = 0;
                _cursorBlinkTimer = 0;
            }

            // End
            if (Input.GetKeyDown(KeyCode.End))
            {
                if (shift && _selectionStart < 0) _selectionStart = _cursorPos;
                else if (!shift) _selectionStart = -1;
                _cursorPos = text.Length;
                _cursorBlinkTimer = 0;
            }

            // Return/Enter — submit
            if (Input.GetKeyDown(KeyCode.Return))
            {
                Unfocus();
            }

            // Escape — cancel
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Unfocus();
            }
        }

        // ── Clipboard ──

        private void CopyToClipboard()
        {
            if (!HasSelection()) return;
            int selMin = Math.Min(_selectionStart, _cursorPos);
            int selMax = Math.Max(_selectionStart, _cursorPos);
            var selected = text.Substring(selMin, selMax - selMin);
            SystemClipboard.SetText(selected);
        }

        private void PasteFromClipboard()
        {
            var clipText = SystemClipboard.GetText();
            if (string.IsNullOrEmpty(clipText)) return;

            // Filter disallowed characters
            var filtered = FilterText(clipText);
            if (string.IsNullOrEmpty(filtered)) return;

            DeleteSelection();

            int available = maxLength - text.Length;
            if (available <= 0) return;
            if (filtered.Length > available)
                filtered = filtered.Substring(0, available);

            text = text.Insert(_cursorPos, filtered);
            _cursorPos += filtered.Length;
            _selectionStart = -1;
            _cursorBlinkTimer = 0;
            NotifyValueChanged();
        }

        // ── Selection Helpers ──

        private bool HasSelection() => _selectionStart >= 0 && _selectionStart != _cursorPos;

        private void DeleteSelection()
        {
            if (!HasSelection()) return;
            int selMin = Math.Min(_selectionStart, _cursorPos);
            int selMax = Math.Max(_selectionStart, _cursorPos);
            text = text.Remove(selMin, selMax - selMin);
            _cursorPos = selMin;
            _selectionStart = -1;
        }

        // ── Focus ──

        private void Focus()
        {
            if (_currentFocused != null && _currentFocused != this)
                _currentFocused.Unfocus();
            _isFocused = true;
            _currentFocused = this;
            _cursorBlinkTimer = 0;
        }

        private void Unfocus()
        {
            if (!_isFocused) return;
            _isFocused = false;
            _selectionStart = -1;
            if (_currentFocused == this)
                _currentFocused = null;
            try { onEndEdit?.Invoke(text); }
            catch (Exception ex) { Debug.LogError($"[UIInputField] onEndEdit error: {ex.Message}"); }
        }

        // ── Character Validation ──

        private bool IsCharAllowed(char c)
        {
            return contentType switch
            {
                InputFieldContentType.IntegerNumber => char.IsDigit(c) || c == '-',
                InputFieldContentType.DecimalNumber => char.IsDigit(c) || c == '-' || c == '.',
                InputFieldContentType.Alphanumeric => char.IsLetterOrDigit(c),
                _ => true,
            };
        }

        private string FilterText(string input)
        {
            if (contentType == InputFieldContentType.Standard || contentType == InputFieldContentType.Password)
                return input;

            var sb = new System.Text.StringBuilder(input.Length);
            foreach (char c in input)
            {
                if (IsCharAllowed(c)) sb.Append(c);
            }
            return sb.ToString();
        }

        // ── Modifier Keys ──

        private static bool IsCtrlHeld() =>
            Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

        private static bool IsShiftHeld() =>
            Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        // ── Text Measurement (baked font glyph-based) ──

        private float MeasureText(string str, int start, int end, float scale)
        {
            if (font == null || start >= end || string.IsNullOrEmpty(str)) return 0f;
            float w = 0f;
            int clampedEnd = Math.Min(end, str.Length);
            for (int i = start; i < clampedEnd; i++)
            {
                if (font.glyphs.TryGetValue(str[i], out var g))
                    w += g.advance * scale;
                else
                    w += font.fontSize * 0.5f * scale;
            }
            return w;
        }

        private int GetCharIndexAtX(string str, float textStartX, float mouseX, float scale)
        {
            if (font == null || string.IsNullOrEmpty(str)) return 0;
            float x = textStartX;
            for (int i = 0; i < str.Length; i++)
            {
                float charW;
                if (font.glyphs.TryGetValue(str[i], out var g))
                    charW = g.advance * scale;
                else
                    charW = font.fontSize * 0.5f * scale;

                if (mouseX < x + charW * 0.5f)
                    return i;
                x += charW;
            }
            return str.Length;
        }

        private void NotifyValueChanged()
        {
            try { onValueChanged?.Invoke(text); }
            catch (Exception ex) { Debug.LogError($"[UIInputField] onValueChanged error: {ex.Message}"); }
        }

        // ── Utility ──

        private static uint ColorToU32(Color c)
        {
            byte r = (byte)(Math.Clamp(c.r, 0f, 1f) * 255f);
            byte g = (byte)(Math.Clamp(c.g, 0f, 1f) * 255f);
            byte b = (byte)(Math.Clamp(c.b, 0f, 1f) * 255f);
            byte a = (byte)(Math.Clamp(c.a, 0f, 1f) * 255f);
            return (uint)(r | (g << 8) | (b << 16) | (a << 24));
        }
    }
}

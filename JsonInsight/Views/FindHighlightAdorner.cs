using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace JsonInsight.Views;

/// <summary>
/// Paints every find match in the editor pane, with the current one stronger than the rest.
///
/// <para>
/// An adorner rather than a different control. A <see cref="TextBox"/> cannot colour part of its own
/// content, so the alternatives were a <c>RichTextBox</c> — a different document model, different
/// undo, and a rewrite of everything that reads <c>Text</c> — or drawing over the top. This draws
/// over the top: the adorner layer sits above the text box, is transparent to the mouse, and needs
/// nothing from the editing path at all.
/// </para>
///
/// <para>
/// Rectangles come from <see cref="TextBox.GetRectFromCharacterIndex"/>, which answers in the text
/// box's own coordinates and already accounts for scroll — so the marks move with the text without
/// this knowing anything about the scroll offset. What it does have to know is <em>when</em> to
/// repaint, which is why it listens for scrolling, wrapping and size changes: none of them changes
/// the character indices, and all of them change where those characters are.
/// </para>
/// </summary>
public sealed class FindHighlightAdorner : Adorner
{
    private readonly TextBox _editor;

    private IReadOnlyList<int> _matches = [];
    private int _length;
    private int _current = -1;

    public FindHighlightAdorner(TextBox editor)
        : base(editor)
    {
        _editor = editor;
        IsHitTestVisible = false;

        // Scrolling does not raise a layout pass on the adorner, and a mark left where the text used
        // to be is worse than no mark at all.
        _editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnMoved));
        _editor.SizeChanged += OnMoved;
        _editor.TextChanged += OnMoved;
    }

    /// <summary>
    /// What to paint. The offsets and the length come from the view model, which is the same list the
    /// Blazor pane highlights and the same one stepping counts against — so the two front ends cannot
    /// disagree about what a match is.
    /// </summary>
    public void Show(IReadOnlyList<int> matches, int length, int current)
    {
        _matches = matches;
        _length = length;
        _current = current;
        InvalidateVisual();
    }

    private void OnMoved(object sender, RoutedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext drawing)
    {
        if (_matches.Count == 0 || _length <= 0)
        {
            return;
        }

        var soft = Brush("Brush.FindSoft");
        var strong = Brush("Brush.Find");

        // Clipped to the text box: GetRectFromCharacterIndex answers for characters scrolled out of
        // view as well, and an unclipped adorner would paint them over the toolbar above the pane.
        drawing.PushClip(new RectangleGeometry(new Rect(_editor.RenderSize)));

        for (var i = 0; i < _matches.Count; i++)
        {
            foreach (var rectangle in RectanglesFor(_matches[i]))
            {
                drawing.DrawRectangle(i == _current ? strong : soft, null, rectangle);
            }
        }

        drawing.Pop();
    }

    /// <summary>
    /// Where one match sits, as one rectangle per line it covers — usually exactly one.
    ///
    /// <para>
    /// The common case is answered with two calls: a match that begins and ends on the same line is
    /// the box between them. The walk is only for the case that needs it — with wrapping on, a long
    /// match runs off the right of one line and continues at the left of the next, and a single
    /// rectangle spanning both would cover the text in between. Worth splitting, because this runs on
    /// every scroll frame and a term appearing two hundred times would otherwise be two hundred
    /// character-by-character walks per frame.
    /// </para>
    /// </summary>
    private IEnumerable<Rect> RectanglesFor(int start)
    {
        var end = start + _length;
        if (end > _editor.Text.Length)
        {
            yield break;
        }

        var first = _editor.GetRectFromCharacterIndex(start);
        var last = _editor.GetRectFromCharacterIndex(end, trailingEdge: true);

        if (!Usable(first))
        {
            yield break;
        }

        // Off the top or the bottom of the viewport. The clip in OnRender would hide it anyway; this
        // is what keeps a 4000-line document from measuring every match on every scroll frame.
        if (first.Bottom < 0 || first.Top > _editor.RenderSize.Height)
        {
            yield break;
        }

        if (Usable(last) && SameLine(first, last))
        {
            yield return new Rect(first.Left, first.Top, Math.Max(last.Right - first.Left, 1), first.Height);
            yield break;
        }

        // Wrapped, or ending somewhere unmeasurable. One rectangle per line, built by walking.
        var bounds = Rect.Empty;

        for (var at = start; at < end; at++)
        {
            var here = _editor.GetRectFromCharacterIndex(at);
            if (!Usable(here))
            {
                continue;
            }

            if (!bounds.IsEmpty && !SameLine(bounds, here))
            {
                yield return bounds;
                bounds = Rect.Empty;
            }

            var next = _editor.GetRectFromCharacterIndex(at + 1);
            var right = Usable(next) && SameLine(here, next) ? next.Left : here.Right;

            var cell = new Rect(here.Left, here.Top, Math.Max(right - here.Left, 1), here.Height);
            bounds = bounds.IsEmpty ? cell : Rect.Union(bounds, cell);
        }

        if (!bounds.IsEmpty)
        {
            yield return bounds;
        }
    }

    /// <summary>
    /// A character index outside the measured text answers with an empty or infinite rectangle rather
    /// than by throwing, so every result has to be checked before it is drawn with.
    /// </summary>
    private static bool Usable(Rect rectangle) =>
        !rectangle.IsEmpty && !double.IsInfinity(rectangle.Left) && !double.IsNaN(rectangle.Left);

    /// <summary>Compared with a tolerance rather than for equality: these are doubles off a layout pass.</summary>
    private static bool SameLine(Rect a, Rect b) => Math.Abs(a.Top - b.Top) < 0.5;

    /// <summary>
    /// Looked up rather than captured, so a theme switch repaints in the new colours. A brush held in
    /// a field would keep the old theme's after Ctrl+D.
    /// </summary>
    private Brush Brush(string key) =>
        _editor.TryFindResource(key) as Brush ?? Brushes.Transparent;
}

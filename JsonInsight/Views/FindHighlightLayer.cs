using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace JsonInsight.Views;

/// <summary>
/// Paints every find match in the editor pane, with the current one stronger than the rest.
///
/// <para>
/// It sits <em>behind</em> the text box rather than over it. A <see cref="TextBox"/> cannot colour
/// part of its own content, so the alternatives were a <c>RichTextBox</c> — a different document
/// model, different undo, and a rewrite of everything that reads <c>Text</c> — or a second element
/// holding the marks. This is the second element, in the same grid cell as the editor, which is
/// transparent so the marks show through. That is the arrangement the Blazor pane uses too, for the
/// same reason: the glyphs stay the editor's to draw, and the layer contributes only backgrounds.
/// </para>
///
/// <para>
/// This was an <c>Adorner</c> once, and both of that arrangement's problems were fatal. An adorner
/// renders <em>above</em> what it adorns, so opaque marks erased the very words they were marking.
/// And an adorner lives in the window's adorner layer rather than in the tree it decorates: selecting
/// another tab disconnects this pane, the layer drops the adorner, and coming back does not put it
/// there again — so the highlights vanished for good after the first tab switch. An ordinary child
/// of the grid has neither problem, because it travels with the pane.
/// </para>
///
/// <para>
/// Rectangles come from <see cref="TextBox.GetRectFromCharacterIndex"/>, which answers in the text
/// box's own coordinates and already accounts for scroll — so the marks move with the text without
/// this knowing anything about the scroll offset, and without the scroll-syncing the web pane needs.
/// What it does have to know is <em>when</em> to repaint, which is why it listens for scrolling,
/// wrapping and size changes: none of them changes the character indices, and all of them change
/// where those characters are.
/// </para>
/// </summary>
public sealed class FindHighlightLayer : FrameworkElement
{
    /// <summary>
    /// The text box whose matches this paints. Set in XAML by <c>ElementName</c>, so the pairing is
    /// visible where the pane is described rather than buried in the code-behind.
    /// </summary>
    public static readonly DependencyProperty EditorProperty = DependencyProperty.Register(
        nameof(Editor),
        typeof(TextBox),
        typeof(FindHighlightLayer),
        new PropertyMetadata(null, OnEditorChanged));

    private IReadOnlyList<int> _matches = [];
    private int _length;
    private int _current = -1;

    public FindHighlightLayer()
    {
        // The editor is on top and takes the clicks; this must never intercept one.
        IsHitTestVisible = false;
    }

    public TextBox? Editor
    {
        get => (TextBox?)GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    private static void OnEditorChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var layer = (FindHighlightLayer)sender;

        if (e.OldValue is TextBox old)
        {
            old.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(layer.OnMoved));
            old.SizeChanged -= layer.OnMoved;
            old.TextChanged -= layer.OnMoved;
        }

        if (e.NewValue is TextBox editor)
        {
            // Scrolling does not raise a layout pass here, and a mark left where the text used to be
            // is worse than no mark at all.
            editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(layer.OnMoved));
            editor.SizeChanged += layer.OnMoved;
            editor.TextChanged += layer.OnMoved;
        }

        layer.InvalidateVisual();
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
        if (Editor is not { } editor || _matches.Count == 0 || _length <= 0)
        {
            return;
        }

        var soft = Brush("Brush.FindSoft");
        var strong = Brush("Brush.Find");

        // Clipped to this element: GetRectFromCharacterIndex answers for characters scrolled out of
        // view as well, and without the clip those would be painted outside the pane.
        drawing.PushClip(new RectangleGeometry(new Rect(RenderSize)));

        for (var i = 0; i < _matches.Count; i++)
        {
            foreach (var rectangle in RectanglesFor(editor, _matches[i]))
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
    private IEnumerable<Rect> RectanglesFor(TextBox editor, int start)
    {
        var end = start + _length;
        if (end > editor.Text.Length)
        {
            yield break;
        }

        var first = editor.GetRectFromCharacterIndex(start);
        var last = editor.GetRectFromCharacterIndex(end, trailingEdge: true);

        if (!Usable(first))
        {
            yield break;
        }

        // Off the top or the bottom of the viewport. The clip in OnRender would hide it anyway; this
        // is what keeps a 4000-line document from measuring every match on every scroll frame.
        if (first.Bottom < 0 || first.Top > RenderSize.Height)
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
            var here = editor.GetRectFromCharacterIndex(at);
            if (!Usable(here))
            {
                continue;
            }

            if (!bounds.IsEmpty && !SameLine(bounds, here))
            {
                yield return bounds;
                bounds = Rect.Empty;
            }

            var next = editor.GetRectFromCharacterIndex(at + 1);
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
    private Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.Transparent;
}

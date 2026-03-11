using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using WhiteBehemoth.Resolver;
using WhiteBehemoth.Resolver.Models;

namespace WhiteBehemoth.Yara;

public class DocumentMatch
{
    public int Start { get; set; }
    public int Length { get; set; }
}

public class HomographColorizer : DocumentColorizingTransformer
{
    private readonly IEnumerable<ResolvedHomograph> _homographs;
    private readonly Func<double> _thresholdFunc;
    private ResolvedHomograph? _highlighted;

    public HomographColorizer(IEnumerable<ResolvedHomograph> homographs, Func<double> thresholdFunc)
    {
        _homographs = homographs;
        _thresholdFunc = thresholdFunc;
    }

    public void SetHighlighted(ResolvedHomograph? h)
    {
        _highlighted = h;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        int lineStart = line.Offset;
        int lineEnd = lineStart + line.Length;
        double threshold = _thresholdFunc();

        foreach (var h in _homographs)
        {
            int hStart = h.AbsolutePosition;
            int hEnd = hStart + h.Length;

            if (hEnd > lineStart && hStart < lineEnd)
            {
                int start = Math.Max(lineStart, hStart);
                int end = Math.Min(lineEnd, hEnd);

                if (start >= end) continue;

                bool isHighlighted = h == _highlighted;
                bool isLowConfidence = h.Confidence * 100 < threshold;

                ChangeLinePart(start, end, visualLine =>
                {
                    if (isHighlighted)
                    {
                        visualLine.TextRunProperties.SetBackgroundBrush(Brushes.Yellow);
                        visualLine.TextRunProperties.SetForegroundBrush(Brushes.Black);
                        visualLine.TextRunProperties.SetTypeface(new Typeface(visualLine.TextRunProperties.Typeface.FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal));
                    }
                    else if (isLowConfidence)
                    {
                        visualLine.TextRunProperties.SetForegroundBrush(Brushes.Black);
                        // Underline unconfident matches, bold them
                        visualLine.TextRunProperties.SetTypeface(new Typeface(visualLine.TextRunProperties.Typeface.FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal));
                        visualLine.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
                    }
                    else
                    {
                        // Confident: just bold or nothing, user said: "Неуверенные - жирным с подчеркиванием, подчеркивание уберать когда омограф разрешен."
                        // So resolved is maybe just bold or standard. Let's make it just bold, no underline.
                        visualLine.TextRunProperties.SetTypeface(new Typeface(visualLine.TextRunProperties.Typeface.FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal));
                        visualLine.TextRunProperties.SetForegroundBrush(Brushes.Black);
                    }
                });
            }
        }
    }
}

public class MatchColorizer : DocumentColorizingTransformer
{
    private readonly IEnumerable<DocumentMatch> _matches;

    public MatchColorizer(IEnumerable<DocumentMatch> matches)
    {
        _matches = matches;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        int lineStart = line.Offset;
        int lineEnd = lineStart + line.Length;

        foreach (var m in _matches)
        {
            if (m.Length == 0) continue; // Skip if handled

            int mStart = m.Start;
            int mEnd = mStart + m.Length;

            if (mEnd > lineStart && mStart < lineEnd)
            {
                int start = Math.Max(lineStart, mStart);
                int end = Math.Min(lineEnd, mEnd);

                if (start >= end) continue;

                ChangeLinePart(start, end, visualLine =>
                {
                    visualLine.TextRunProperties.SetBackgroundBrush(Brushes.LightGray);
                    visualLine.TextRunProperties.SetForegroundBrush(Brushes.Black);
                });
            }
        }
    }
}

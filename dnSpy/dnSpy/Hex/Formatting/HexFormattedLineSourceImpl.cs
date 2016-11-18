﻿/*
    Copyright (C) 2014-2016 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Media.TextFormatting;
using dnSpy.Contracts.Hex;
using dnSpy.Contracts.Hex.Classification;
using dnSpy.Contracts.Hex.Formatting;
using dnSpy.Text.Formatting;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Formatting;

namespace dnSpy.Hex.Formatting {
	sealed class HexFormattedLineSourceImpl : HexFormattedLineSource {
		public override TextRunProperties DefaultTextProperties => classificationFormatMap.DefaultTextProperties;
		public override HexAndAdornmentSequencer HexAndAdornmentSequencer { get; }
		public override double BaseIndentation { get; }
		public override double ColumnWidth { get; }
		public override double LineHeight { get; }
		public override double TextHeightAboveBaseline { get; }
		public override double TextHeightBelowBaseline { get; }
		public override bool UseDisplayMode { get; }
		const int TabSize = 4;

		readonly HexClassifier aggregateClassifier;
		readonly IClassificationFormatMap classificationFormatMap;
		readonly FormattedTextCache formattedTextCache;
		readonly TextFormatter textFormatter;
		readonly TextParagraphProperties defaultTextParagraphProperties;

		// Should be enough...
		const int MAX_LINE_LENGTH = 5000;

		public HexFormattedLineSourceImpl(ITextFormatterProvider textFormatterProvider, double baseIndent, bool useDisplayMode, HexClassifier aggregateClassifier, HexAndAdornmentSequencer sequencer, IClassificationFormatMap classificationFormatMap) {
			if (textFormatterProvider == null)
				throw new ArgumentNullException(nameof(textFormatterProvider));
			if (aggregateClassifier == null)
				throw new ArgumentNullException(nameof(aggregateClassifier));
			if (sequencer == null)
				throw new ArgumentNullException(nameof(sequencer));
			if (classificationFormatMap == null)
				throw new ArgumentNullException(nameof(classificationFormatMap));

			textFormatter = textFormatterProvider.Create(useDisplayMode);
			formattedTextCache = new FormattedTextCache(useDisplayMode);
			UseDisplayMode = useDisplayMode;
			BaseIndentation = baseIndent;
			ColumnWidth = formattedTextCache.GetColumnWidth(classificationFormatMap.DefaultTextProperties);
			LineHeight = HexFormattedLineImpl.DEFAULT_TOP_SPACE + HexFormattedLineImpl.DEFAULT_BOTTOM_SPACE + formattedTextCache.GetLineHeight(classificationFormatMap.DefaultTextProperties);
			TextHeightAboveBaseline = formattedTextCache.GetTextHeightAboveBaseline(classificationFormatMap.DefaultTextProperties);
			TextHeightBelowBaseline = formattedTextCache.GetTextHeightBelowBaseline(classificationFormatMap.DefaultTextProperties);
			HexAndAdornmentSequencer = sequencer;
			this.aggregateClassifier = aggregateClassifier;
			this.classificationFormatMap = classificationFormatMap;
			defaultTextParagraphProperties = new TextFormattingParagraphProperties(classificationFormatMap.DefaultTextProperties, ColumnWidth * TabSize);
		}

		public override HexFormattedLine FormatLineInVisualBuffer(HexBufferLine line) {
			if (line == null)
				throw new ArgumentNullException(nameof(line));

			var seqColl = HexAndAdornmentSequencer.CreateHexAndAdornmentCollection(line);
			var linePartsCollection = CreateLinePartsCollection(seqColl, line);
			var textSource = new HexLinePartsTextSource(linePartsCollection);

			TextLineBreak previousLineBreak = null;
			double autoIndent = BaseIndentation;
			int column = 0;
			int linePartsIndex = 0;
			var lineParts = linePartsCollection.LineParts;

			textSource.SetMaxLineLength(MAX_LINE_LENGTH);
			var textLine = textFormatter.FormatLine(textSource, column, 0, defaultTextParagraphProperties, previousLineBreak);

			int startColumn = column;
			int length = textLine.GetLength(textSource.EndOfLine);
			column += length;

			int linePartsEnd = linePartsIndex;
			Debug.Assert(lineParts.Count == 0 || linePartsEnd < lineParts.Count);
			while (linePartsEnd < lineParts.Count) {
				var part = lineParts[linePartsEnd];
				linePartsEnd++;
				if (column <= part.Column + part.ColumnLength)
					break;
			}
			linePartsEnd--;

			var startPos = textSource.ConvertColumnToLinePosition(startColumn);
			var endPos = textSource.ConvertColumnToLinePosition(column);
			if (column >= textSource.Length) {
				endPos = line.TextSpan.End;
				linePartsEnd = lineParts.Count - 1;
			}

			var lineSpan = Span.FromBounds(startPos, endPos);
			var formattedLine = new HexFormattedLineImpl(linePartsCollection, linePartsIndex, linePartsEnd - linePartsIndex + 1, startColumn, column, line, lineSpan, textLine, autoIndent, ColumnWidth);
			Debug.Assert(column == textSource.Length);

			return formattedLine;
		}

		HexLinePartsCollection CreateLinePartsCollection(HexAndAdornmentCollection coll, HexBufferLine bufferLine) {
			var lineExtent = bufferLine.TextSpan;
			if (coll.Count == 0)
				return new HexLinePartsCollection(emptyLineParts, lineExtent, bufferLine.Text);

			var list = new List<HexLinePart>();

			int column = 0;
			int startOffs = lineExtent.Start;
			foreach (var seqElem in coll) {
				if (seqElem.ShouldRenderText) {
					var cspans = new List<HexClassificationSpan>();
					var textSpan = bufferLine.TextSpan.Intersection(seqElem.Span) ?? new Span(bufferLine.TextSpan.End, 0);
					aggregateClassifier.GetClassificationSpans(cspans, new HexClassificationContext(bufferLine, textSpan));
					int lastOffs = seqElem.Span.Start;
					for (int i = 0; i < cspans.Count; i++) {
						var cspan = cspans[i];
						int otherSize = cspan.Span.Start - lastOffs;
						if (otherSize != 0) {
							Debug.Assert(otherSize > 0);
							list.Add(new HexLinePart(list.Count, column, new Span(lastOffs - startOffs, otherSize), DefaultTextProperties));
							column += otherSize;
						}
						Add(list, column, cspan, lineExtent);
						column += cspan.Span.Length;
						lastOffs = cspan.Span.End;
					}
					int lastSize = seqElem.Span.End - lastOffs;
					if (lastSize != 0) {
						list.Add(new HexLinePart(list.Count, column, new Span(lastOffs - startOffs, lastSize), DefaultTextProperties));
						column += lastSize;
					}
				}
				else {
					var adornmentElement = seqElem as HexAdornmentElement;
					if (adornmentElement != null) {
						var span = seqElem.Span;
						list.Add(new HexLinePart(list.Count, column, new Span(span.Start - startOffs, span.Length), adornmentElement, DefaultTextProperties));
						column += list[list.Count - 1].ColumnLength;
					}
				}
			}
			Debug.Assert(list.Sum(a => a.ColumnLength) == column);

			return new HexLinePartsCollection(list, lineExtent, bufferLine.Text);
		}
		static readonly List<HexLinePart> emptyLineParts = new List<HexLinePart>();

		void Add(List<HexLinePart> list, int column, HexClassificationSpan cspan, Span lineExtent) {
			if (cspan.Span.Length == 0)
				return;
			int startOffs = lineExtent.Start;
			var props = classificationFormatMap.GetTextProperties(cspan.ClassificationType);
			if (list.Count > 0) {
				var last = list[list.Count - 1];
				if (last.AdornmentElement == null && last.TextRunProperties == props && last.Span.End == cspan.Span.Start) {
					list[list.Count - 1] = new HexLinePart(list.Count - 1, last.Column, Span.FromBounds(last.Span.Start - startOffs, cspan.Span.End - startOffs), last.TextRunProperties);
					return;
				}
			}
			list.Add(new HexLinePart(list.Count, column, new Span(cspan.Span.Start - startOffs, cspan.Span.Length), props));
		}
	}
}
﻿// Copyright (c) Stephan Tolksdorf 2007-2009
// License: Simplified BSD License. See accompanying documentation.

#if LOW_TRUST

using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace FParsec {

public sealed class CharStream : IDisposable {
    private const int DefaultByteBufferLength = (1 << 12);
    private const char EOS = '\uFFFF';

    public Encoding Encoding { get; private set; }
    internal String String;
    internal int IndexBegin;
    internal int IndexEnd;
    internal long StreamIndexOffset;

    public long IndexOffset { get { return StreamIndexOffset; } }
    public long EndOfStream { get { return StreamIndexOffset + (IndexEnd - IndexBegin); } }

    internal CharStream(string chars) {
        Debug.Assert(chars != null);
        String = chars;
        //IndexBegin = 0;
        IndexEnd = chars.Length;
        //StreamIndexOffset = 0L;
    }

    /// <summary>Constructs a CharStream from the chars in the string argument between the indices index (inclusive) and index + length (exclusive).</summary>
    /// <exception cref="ArgumentNullException">chars is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">At least one of the following conditions is not satisfied: index ≥ 0, length ≥ 0 and index + length ≤ chars.Length.</exception>
    public CharStream(string chars, int index, int length) : this(chars, index, length, 0) {}

    /// <summary>Constructs a CharStream from the chars in the string argument between the indices index (inclusive) and index + length (exclusive). The first char in the stream is assigned the index streamIndexOffset.</summary>
    /// <exception cref="ArgumentNullException">chars is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">At least one of the following conditions is not satisfied: index ≥ 0, length ≥ 0, index + length ≤ chars.Length and 0 ≤ streamIndexOffset &lt; 2^60.</exception>
    public CharStream(string chars, int index, int length, long streamIndexOffset) {
        if (chars == null) throw new ArgumentNullException("chars");
        if (index < 0) throw new ArgumentOutOfRangeException("index", "The index is negative.");
        if (streamIndexOffset < 0 || streamIndexOffset >= (1L << 60)) throw new ArgumentOutOfRangeException("streamIndexOffset", "The index offset must be non-negative and less than 2^60.");
        int indexEnd = unchecked (index + length);
        if (indexEnd < index || indexEnd > chars.Length) throw new ArgumentOutOfRangeException("length", "The length is out of range.");

        String = chars;
        IndexBegin = index;
        IndexEnd = indexEnd;
        StreamIndexOffset = streamIndexOffset;
    }


    /// <summary>Constructs a CharStream from the file at the given path.<br/>Is equivalent to CharStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan), false, encoding, true, defaultBlockSize, defaultBlockSize/3, ((defaultBlockSize/3)*2)/3, defaultByteBufferLength).</summary>
    public CharStream(string path, Encoding encoding)
           : this(path, encoding, true, DefaultByteBufferLength) { }

    /// <summary>Constructs a CharStream from the file at the given path.<br/>Is equivalent to CharStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan), false, encoding, detectEncodingFromByteOrderMarks, defaultBlockSize, defaultBlockSize/3, ((defaultBlockSize/3)*2)/3, defaultByteBufferLength).</summary>
    public CharStream(string path, Encoding encoding, bool detectEncodingFromByteOrderMarks)
           : this(path, encoding, detectEncodingFromByteOrderMarks, DefaultByteBufferLength) { }

    /// <summary>Constructs a CharStream from the file at the given path.<br/>Is equivalent to CharStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan), false, encoding, detectEncodingFromByteOrderMarks, blockSize, blockOverlap, minRegexSpace, byteBufferLength).</summary>
    public CharStream(string path, Encoding encoding, bool detectEncodingFromByteOrderMarks, int byteBufferLength) {
        if (encoding == null) throw new ArgumentNullException("encoding");
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        try {
           StreamConstructorContinue(stream, false, encoding, detectEncodingFromByteOrderMarks, byteBufferLength);
        } catch {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Constructs a CharStream from a byte Stream.<br/>Is equivalent to CharStream(stream, false, encoding, true, defaultBlockSize, defaultBlockSize/3, ((defaultBlockSize/3)*2)/3, defaultByteBufferLength).</summary>
    public CharStream(Stream stream, Encoding encoding)
           : this(stream, false, encoding, true, DefaultByteBufferLength) { }

    /// <summary>Constructs a CharStream from a byte Stream.<br/>Is equivalent to CharStream(stream, leaveOpen, encoding, true, defaultBlockSize, defaultBlockSize/3, ((defaultBlockSize/3)*2)/3, defaultByteBufferLength).</summary>
    public CharStream(Stream stream, bool leaveOpen, Encoding encoding)
           : this(stream, leaveOpen, encoding, true, DefaultByteBufferLength) { }

    /// <summary>Constructs a CharStream from a byte Stream.<br/>Is equivalent to CharStream(stream, leaveOpen, encoding, detectEncodingFromByteOrderMarks, defaultBlockSize, defaultBlockSize/3, ((defaultBlockSize/3)*2)/3, defaultByteBufferLength).</summary>
    public CharStream(Stream stream, bool leaveOpen, Encoding encoding, bool detectEncodingFromByteOrderMarks)
           : this(stream, leaveOpen, encoding, detectEncodingFromByteOrderMarks, DefaultByteBufferLength) { }

    /// <summary>Constructs a CharStream from a byte Stream.</summary>
    /// <param name="stream">The byte stream providing the input.</param>
    /// <param name="leaveOpen">Indicates whether the byte Stream should be left open when the CharStream has finished reading it.</param>
    /// <param name="encoding">The (default) Encoding used for decoding the byte Stream into chars.</param>
    /// <param name="detectEncodingFromByteOrderMarks">Indicates whether the constructor should detect the encoding from a unicode byte-order mark at the beginning of the stream. An encoding detected from a byte-order mark overrides the default encoding.</param>
    /// <param name="byteBufferLength">The size of the byte buffer used for decoding purposes. The default is 2^12 = 4KB.</param>
    public CharStream(Stream stream, bool leaveOpen, Encoding encoding, bool detectEncodingFromByteOrderMarks, int byteBufferLength) {
        if (stream == null) throw new ArgumentNullException("stream");
        if (!stream.CanRead) throw new ArgumentException("stream is not readable");
        if (encoding == null) throw new ArgumentNullException("encoding");
        StreamConstructorContinue(stream, leaveOpen, encoding, detectEncodingFromByteOrderMarks, byteBufferLength);
    }

    private void StreamConstructorContinue(Stream stream, bool leaveOpen, Encoding encoding, bool detectEncodingFromByteOrderMarks, int byteBufferLength) {
        // the ByteBuffer must be larger than the longest detectable preamble
        if (byteBufferLength < 16) byteBufferLength = DefaultByteBufferLength;

        long streamPosition = 0;
        int bytesInStream = -1;
        if (stream.CanSeek) {
            streamPosition = stream.Position;
            long streamLength = stream.Length - streamPosition;
            if (streamLength <= Int32.MaxValue) {
                bytesInStream = (int) streamLength;
                if (bytesInStream < byteBufferLength) byteBufferLength = bytesInStream;
            }
        }

        byte[] byteBuffer = new byte[byteBufferLength];

        int byteBufferCount = 0;
        do {
            int c = stream.Read(byteBuffer, byteBufferCount, byteBuffer.Length - byteBufferCount);
            if (c > 0) byteBufferCount += c;
            else {
                bytesInStream = byteBufferCount;
                break;
            }
        } while (byteBufferCount < 16);
        int preambleLength = Helper.DetectPreamble(byteBuffer, byteBufferCount, ref encoding, detectEncodingFromByteOrderMarks);
        bytesInStream -= preambleLength;
        streamPosition += preambleLength;
        Encoding = encoding;

        if (bytesInStream == 0) {
            String = "";
            //Index = 0;
            //Length = 0;
            //StreamIndexOffset = 0L;
            return;
        }

        Decoder decoder = encoding.GetDecoder();
        int charBufferLength = encoding.GetMaxCharCount(byteBufferLength); // might throw
        char[] charBuffer = new char[charBufferLength];

        int stringBufferCapacity = 2*charBufferLength;
        if (bytesInStream > 0) {
            try {
                stringBufferCapacity = encoding.GetMaxCharCount(bytesInStream); // might throw
            } catch (ArgumentOutOfRangeException) { }
        }
        StringBuilder sb = new StringBuilder(stringBufferCapacity);
        if (byteBufferCount > preambleLength) {
            int charBufferCount;
            try {
                charBufferCount = decoder.GetChars(byteBuffer, preambleLength, byteBufferCount - preambleLength, charBuffer, 0, false);
                streamPosition += byteBufferCount - preambleLength;
            } catch (DecoderFallbackException e) {
                e.Data.Add("Stream.Position", streamPosition + e.Index);
                throw;
            }
            sb.Append(charBuffer, 0, charBufferCount);
        }
        for (;;) {
            byteBufferCount = stream.Read(byteBuffer, 0, byteBuffer.Length);
            bool flush = byteBufferCount == 0;
            int charBufferCount;
            try {
                charBufferCount = decoder.GetChars(byteBuffer, 0, byteBufferCount, charBuffer, 0, flush);
                streamPosition += byteBufferCount;
            } catch (DecoderFallbackException e) {
                e.Data.Add("Stream.Position", streamPosition + e.Index);
                throw;
            }
            sb.Append(charBuffer, 0, charBufferCount);
            if (flush) break;
        }
        String = sb.ToString();
        //Index = 0;
        IndexEnd = String.Length;
        //StreamIndexOffset = 0L;

        if (!leaveOpen) stream.Close();
    }

    /// <summary>The low trust version of the CharStream class implements the IDisposable
    /// interface only for API compatibility. The Dispose method does not need to be called on
    /// low trust CharStream instances, because the instances hold no resources that need to be disposed.</summary>
    public void Dispose() {
        String = null;
    }

    /// <summary>An iterator pointing to the beginning of the stream (or to the end if the CharStream is empty).</summary>
    public Iterator Begin { get {
        if (IndexBegin < IndexEnd)
            return new Iterator {Stream = this, Idx = IndexBegin};
        else
            return new Iterator {Stream = this, Idx = Int32.MinValue};
    } }

    /// <summary>Returns an iterator pointing to the given index in the stream,
    /// or to the end of the stream if the indexed position lies beyond the last char in the stream.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is less than 0 (or less than the index offset specified when the CharStream was constructed).</exception>
    public Iterator Seek(long index) {
        long off = unchecked(index - StreamIndexOffset);
        if (off < 0)
            throw (new ArgumentOutOfRangeException("index", "The index is negative (or less than the char index offset specified at construction time)."));
        int streamLength = IndexEnd - IndexBegin;
        if (off < streamLength)
            return new Iterator {Stream = this, Idx = IndexBegin + (int)off};
        else
            return new Iterator {Stream = this, Idx = Int32.MinValue};
    }

    /// <summary>The iterator type for CharStreams.</summary>
    public struct Iterator : IEquatable<Iterator>  {
        public CharStream Stream { get; internal set; }
        internal int Idx;

        /// <summary>Indicates whether the Iterator has reached the end of the stream,
        /// i.e. whether it points to one char beyond the last char in the stream.</summary>
        public bool IsEndOfStream { get { return Idx < 0; } }

        /// <summary>The char returned by Read() if the iterator has
        /// reached the end of the stream. The value is '\uFFFF'.</summary>
        public const char EndOfStreamChar = EOS;

        /// <summary>The index of the stream char pointed to by the Iterator.</summary>
        public long Index { get {
            if (Idx >= 0)
                return Stream.StreamIndexOffset + (Idx - Stream.IndexBegin);
            else
                return Stream.StreamIndexOffset + (Stream.IndexEnd - Stream.IndexBegin);
        } }

        /// <summary>Returns an Iterator pointing to the next char in the stream. If the Iterator already
        /// has reached the end of the stream, i.e. if it points to one char beyond
        /// the last char, the same Iterator is returned.</summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public Iterator Next { get {
            int idx = Idx + 1;
            if (unchecked((uint)idx) < (uint)Stream.IndexEnd)
                return new Iterator {Stream = Stream, Idx = idx};
            else
                return new Iterator {Stream = Stream, Idx = Int32.MinValue};
        } }

        /// <summary>Returns an Iterator that is advanced by numberOfChars chars. The Iterator can't
        /// move past the end of the stream, i.e. any position beyond the last char
        /// in the stream is interpreted as precisely one char beyond the last char.</summary>
        /// <exception cref="ArgumentOutOfRangeException">The new index is negative (or less than the index offset specified when the CharStream was constructed).</exception>
        public Iterator Advance(int numberOfChars) {
            var stream = Stream;
            int idx = unchecked(Idx + numberOfChars);
            if (numberOfChars < 0) goto Negative;
            if (unchecked((uint)idx) >= (uint)stream.IndexEnd) goto EndOfStream;
        ReturnIter:
            return new Iterator {Stream = stream, Idx = idx};
        EndOfStream:
            idx = Int32.MinValue;
            goto ReturnIter;
        Negative:
            if (Idx >= 0) {
                if (idx >= stream.IndexBegin) goto ReturnIter;
            } else {
                idx = stream.IndexEnd + numberOfChars;
                if (idx >= stream.IndexBegin) goto ReturnIter;
            }
            throw new ArgumentOutOfRangeException("numberOfChars");
        }

        /// <summary>Returns an Iterator that is advanced by numberOfChars chars. The Iterator can't
        /// move past the end of the stream, i.e. any position beyond the last char
        /// in the stream is interpreted as precisely one char beyond the last char.</summary>
        /// <exception cref="ArgumentOutOfRangeException">The new index is negative (or less than the index offset specified when the CharStream was constructed).</exception>
        public Iterator Advance(long numberOfChars) {
            if (unchecked((int)numberOfChars) != numberOfChars) goto LargeNumber;
            int idx = unchecked(Idx + (int)numberOfChars);
            if ((int)numberOfChars < 0) goto Negative;
            if (unchecked((uint)idx) >= (uint)Stream.IndexEnd) goto EndOfStream;
        ReturnIter:
            return new Iterator {Stream = Stream, Idx = idx};
        EndOfStream:
            idx = Int32.MinValue;
            goto ReturnIter;
        Negative:
            if (Idx >= 0) {
                if (idx >= Stream.IndexBegin) goto ReturnIter;
            } else {
                idx = Stream.IndexEnd + (int)numberOfChars;
                if (idx >= Stream.IndexBegin) goto ReturnIter;
            }
        OutOfRange:
            throw new ArgumentOutOfRangeException("numberOfChars");
        LargeNumber:
            if (numberOfChars >= 0) goto EndOfStream;
            else goto OutOfRange;
        }

        /// <summary>Returns an Iterator that is advanced by numberOfChars chars. The Iterator can't
        /// move past the end of the stream, i.e. any position beyond the last char
        /// in the stream is interpreted as precisely one char beyond the last char.</summary>
        public Iterator Advance(uint numberOfChars) {
            int indexEnd = Stream.IndexEnd;
            int n = unchecked((int)numberOfChars);
            if (n >= 0) { // numberOfChars <= Int32.MaxValue
                int idx = unchecked(Idx + n);
                if (unchecked((uint)idx) < (uint)indexEnd) {
                    return new Iterator {Stream = Stream, Idx = idx};
                }
            }
            return new Iterator {Stream = Stream, Idx = Int32.MinValue};
        }

        /// <summary>Advances the iterator *in-place* by 1 char and returns the char on the new position.
        ///`c = iter.Increment()` is equivalent to `iter = iter.Next; c = iter.Read()`.</summary>
        public char _Increment() {
            int idx = Idx + 1;
            var stream = Stream;
            if (unchecked((uint)idx) < (uint)stream.IndexEnd) {
                Idx = idx;
                return stream.String[idx];
            } else {
                Idx = Int32.MinValue;
                return EOS;
            }
        }

        /// <summary>Advances the Iterator *in-place* by n chars and returns the char on the new position.
        /// `c = iter.Increment(numberOfChars)` is an optimized implementation of `iter = iter.Advance(numberOfChars); c = iter.Read()`.</summary>
        public char _Increment(uint numberOfChars) {
            var stream = Stream;
            int n = unchecked((int)numberOfChars);
            if (n >= 0) { // numberOfChars <= Int32.MaxValue
                int idx = unchecked(Idx + n);
                if (unchecked((uint)idx) < (uint)stream.IndexEnd) {
                    Idx = idx;
                    return stream.String[idx];
                }
            }
            Idx = Int32.MinValue;
            return EOS;
        }

        /// <summary>Advances the Iterator *in-place* by -1 char and returns the char on the new position.
        /// `c = iter.Decrement()` is an optimized implementation of `iter = iter.Advance(-1); c = iter.Read()`.</summary>
        /// <exception cref="ArgumentOutOfRangeException">The new index is less than 0 (or less than the index offset specified when the CharStream was constructed).</exception>
        public char _Decrement() {
            int idx = Idx;
            var stream = Stream;
            if (idx > stream.IndexBegin) {
                idx -= 1;
                Idx = idx;
                return stream.String[idx];
            } else if (idx < 0) {
                idx = stream.IndexEnd - 1;
                if (idx >= stream.IndexBegin)  {
                    Idx = idx;
                    return stream.String[idx];
                }
            }
            throw new ArgumentOutOfRangeException("implicit numberOfChars = 1");
        }

        /// <summary>Advances the Iterator *in-place* by -numberOfChars chars and returns the char on the new position.
        /// `c = iter.Decrement()` is an optimized implementation of `iter = iter.Advance(-numberOfChars); c = iter.Read()`.</summary>
        /// <exception cref="ArgumentOutOfRangeException">The new index is less than 0 (or less than the index offset specified when the CharStream was constructed).</exception>
        public char _Decrement(uint numberOfChars) {
            int idx = unchecked(Idx - (int)numberOfChars);
            var stream = Stream;
            if (idx < Idx && idx >= stream.IndexBegin) {
                Idx = idx;
                return stream.String[idx];
            } else if (numberOfChars != 0) {
                if (Idx < 0) {
                    int indexEnd = stream.IndexEnd;
                    idx = unchecked(indexEnd - (int)numberOfChars);
                    if (idx < indexEnd && idx >= stream.IndexBegin) {
                        Idx = idx;
                        return stream.String[idx];
                    }
                }
            } else return Read();
            throw new ArgumentOutOfRangeException("numberOfChars");
        }

        /// <summary>Is an optimized implementation of Next.Read().</summary>
        public char Peek() {
            int idx = Idx + 1;
            var stream = Stream;
            if (unchecked((uint)idx) < (uint)stream.IndexEnd)
                return stream.String[idx];
            else
                return EOS;
        }

        /// <summary>Is an optimized implementation of Advance(numberOfChars).Read(),
        /// except that the EndOfStreamChar ('\uFFFF') is returned if Index + numberOfChars &lt; 0 (instead of an exception being thrown).</summary>
        public char Peek(int numberOfChars) {
            var stream = Stream;
            int idx = unchecked(Idx + numberOfChars);
            if (numberOfChars < 0) goto Negative;
            if (unchecked((uint)idx) >= (uint)stream.IndexEnd) goto EndOfStream;
        ReturnChar:
            return stream.String[idx];
        Negative:
            if (Idx >= 0) {
                if (idx >= stream.IndexBegin) goto ReturnChar;
            } else {
                idx = stream.IndexEnd + numberOfChars;
                if (idx >= stream.IndexBegin) goto ReturnChar;
            }
        EndOfStream:
            return EOS;
        }

        /// <summary>Is an optimized implementation of Advance(numberOfChars).Read().</summary>
        public char Peek(uint numberOfChars) {
            var stream = Stream;
            int n = unchecked((int)numberOfChars);
            if (n >= 0) { // numberOfChars <= Int32.MaxValue
                int idx = unchecked(Idx + n);
                if (unchecked((uint)idx) < (uint)stream.IndexEnd)
                    return stream.String[idx];
            }
            return EOS;
        }

        /// <summary>Returns true if and only if the char argument matches the char pointed to by the Iterator.
        /// At the end of the stream Match always returns false.</summary>
        public bool Match(char c) {
            int idx = Idx;
            if (idx >= 0) return Stream.String[idx] == c;
            else return false;
        }

        /// <summary>Returns true if str matches the chars in the stream beginning with the char pointed to by the Iterator.
        /// If the chars do not match or if there are not enough chars remaining in the stream, false is returned.
        /// If str is empty, true is returned.</summary>
        /// <exception cref="NullReferenceException">str is null.</exception>
        public bool Match(string str) {
            if (unchecked((uint)Idx) + (uint)str.Length <= (uint)Stream.IndexEnd) {
                var s = Stream.String;
                for (int i = 0; i < str.Length; ++i)
                    if (str[i] != s[Idx + i]) goto ReturnFalse;
                return true;
            }
            if (str.Length == 0) return true;
        ReturnFalse:
            return false;
        }

        /// <summary>Returns true if caseFoldedStr matches the chars in the stream
        /// beginning with the char pointed to by the Iterator.
        /// The chars in the stream are case-folded before they are matched,
        /// while the chars in the string argument are assumed to already be case-folded.
        /// If the chars do not match or if there are not enough chars remaining in the stream, false is returned.
        /// If caseFoldedStr is empty, true is returned.</summary>
        /// <exception cref="NullReferenceException">caseFoldedStr is null.</exception>
        public bool MatchCaseFolded(string caseFoldedStr) {
            if (unchecked((uint)Idx) + (uint)caseFoldedStr.Length <= (uint)Stream.IndexEnd) {
                char[] cftable = CaseFoldTable.FoldedChars;
                if (cftable == null) cftable = CaseFoldTable.Initialize();
                var s = Stream.String;
                for (int i = 0; i < caseFoldedStr.Length; ++i)
                    if (caseFoldedStr[i] != cftable[s[Idx + i]]) goto ReturnFalse;
                return true;
            }
            if (caseFoldedStr.Length == 0) return true;
        ReturnFalse:
            return false;
        }

        /// <summary>Returns true if the chars in str between the indices strIndex (inclusive) and
        /// strIndex + length (exclusive) match the chars in the stream beginning with the char pointed to by the Iterator.
        /// If the chars do not match or if there are not enough chars remaining in the stream, false is returned.
        /// If length is 0, true is returned.</summary>
        /// <exception cref="ArgumentOutOfRangeException">strIndex is negative, length is negative or strIndex + length > str.Length.</exception>
        /// <exception cref="NullReferenceException">str is null.</exception>
        public bool Match(string str, int strIndex, int length) {
            if (strIndex < 0)
                throw new ArgumentOutOfRangeException("charsIndex", "charsIndex is negative.");
            if (length < 0 || strIndex > str.Length - length)
                throw new ArgumentOutOfRangeException("length", "length is out of range.");
            if (unchecked((uint)Idx) + (uint)length <= (uint)Stream.IndexEnd) {
                var s = Stream.String;
                for (int i = 0; i < length; ++i)
                    if (str[strIndex + i] != s[Idx + i]) goto ReturnFalse;
                return true;
            }
            if (length == 0) return true;
        ReturnFalse:
            return false;
        }

        /// <summary>Returns true if the chars in the char array between the indices charsIndex (inclusive) and
        /// charsIndex + length (exclusive) match the chars in the stream beginning with the char pointed to by the Iterator.
        /// If the chars do not match or if there are not enough chars remaining in the stream, false is returned.
        /// If length is 0, true is returned.</summary>
        /// <exception cref="ArgumentOutOfRangeException">charsIndex is negative, length is negative or charsIndex + length > chars.Length.</exception>
        /// <exception cref="NullReferenceException">chars is null.</exception>
        public bool Match(char[] chars, int charsIndex, int length) {
            if (charsIndex < 0)
                throw new ArgumentOutOfRangeException("charsIndex", "charsIndex is negative.");
            if (length < 0 || charsIndex > chars.Length - length)
                throw new ArgumentOutOfRangeException("length", "length is out of range.");
            if (unchecked((uint)Idx) + (uint)length <= (uint)Stream.IndexEnd) {
                var s = Stream.String;
                for (int i = 0; i < length; ++i)
                    if (chars[charsIndex + i] != s[Idx + i]) goto ReturnFalse;
                return true;
            }
            if (length == 0) return true;
        ReturnFalse:
            return false;
        }

        /// <summary>Applies the given regular expression to stream chars beginning with the char pointed to by the Iterator.
        /// Returns the resulting Match object.
        /// IMPORTANT: This method is not supported by CharStreams constructed from char arrays or pointers.</summary>
        /// <remarks>For performance reasons you should specifiy the regular expression
        /// such that it can only match at the beginning of a string,
        /// for example by prepending "\A".<br/>
        /// For CharStreams constructed from large binary streams the regular expression is not applied
        /// to a string containing all the remaining chars in the stream. The minRegexSpace parameter
        /// of the CharStream constructors determines the minimum number of chars that are guaranteed
        /// to be visible to the regular expression.</remarks>
        /// <exception cref="NullReferenceException">regex is null.</exception>
        public Match Match(Regex regex) {
            int idx = Idx;
            var stream = Stream;
            if (idx >= 0) return regex.Match(stream.String, idx, stream.IndexEnd - idx);
            else return regex.Match("");
        }

        /// <summary>Returns the stream char pointed to by the Iterator,
        /// or the EndOfStreamChar ('\uFFFF') if the Iterator has reached the end of the stream.</summary>
        public char Read() {
            int idx = Idx;
            if (idx >= 0) return Stream.String[idx];
            else return EOS;
        }

        /// <summary>Returns a string with the length stream chars beginning with the char pointed to by the Iterator.
        /// If less than length chars are remaining in the stream, only the remaining chars are returned.</summary>
        /// <exception cref="ArgumentOutOfRangeException">length is negative.</exception>
        /// <exception cref="OutOfMemoryException">There is not enough memory for the string or the requested string is too large.</exception>
        public string Read(int length) {
            if (length < 0) throw new ArgumentOutOfRangeException("length", "length is negative.");
            int idx = Idx;
            var stream = Stream;
            if (unchecked((uint)idx) + (uint)length <= (uint)stream.IndexEnd)
                return stream.String.Substring(idx, length);
            else
                return idx >= 0 ? stream.String.Substring(idx, stream.IndexEnd - idx) : "";
        }

        /// <summary>Returns a string with the length stream chars beginning with the char pointed to by the Iterator.
        /// If less than length chars are remaining in the stream,
        /// only the remaining chars are returned, or an empty string if allOrEmpty is true.</summary>
        /// <exception cref="ArgumentOutOfRangeException">length is negative.</exception>
        /// <exception cref="OutOfMemoryException">There is not enough memory for the string or the requested string is too large.</exception>
        public string Read(int length, bool allOrEmpty) {
            if (length < 0) throw new ArgumentOutOfRangeException("length", "length is negative.");
            if (unchecked((uint)Idx) + (uint)length <= (uint)Stream.IndexEnd)
                return Stream.String.Substring(Idx, length);
            else
                return Idx >= 0 && !allOrEmpty ? Stream.String.Substring(Idx, Stream.IndexEnd - Idx) : "";
        }

        /// <summary>Copies the length stream chars beginning with the char pointed to by the Iterator into dest.
        /// The chars are written into dest beginning at the index destIndex.
        /// If less than length chars are remaining in the stream, only the remaining chars are copied.
        /// Returns the actual number of chars copied.</summary>
        /// <exception cref="ArgumentOutOfRangeException">destIndex is negative, length is negative or destIndex + length > dest.Length.</exception>
        /// <exception cref="NullReferenceException">dest is null.</exception>
        public int Read(char[] dest, int destIndex, int length) {
            if (destIndex < 0)
                throw new ArgumentOutOfRangeException("destIndex", "destIndex is negative.");
            if (length < 0 || destIndex > dest.Length - length)
                throw new ArgumentOutOfRangeException("length", "length is out of range.");
            if (unchecked((uint)Idx) + (uint)length <= (uint)Stream.IndexEnd) {
                var s = Stream.String;
                for (int i = 0; i < length; ++i)
                    dest[destIndex + i] = s[Idx + i];
                return length;
            } else if (Idx >= 0){
                int n = Stream.IndexEnd - Idx;
                var s = Stream.String;
                for (int i = 0; i < n; ++i)
                    dest[destIndex + i] = s[Idx + i];
                return n;
            } else {
                return 0;
            }
        }

        /// <summary>Returns a string with all the chars in the stream between the position of this Iterator (inclusive)
        /// and the position of the Iterator in the argument (exclusive).
        /// If the Iterator argument does not point to a position after the position of this Iterator, the returned string is empty.</summary>
        /// <exception cref="ArgumentOutOfRangeException">iterToCharAfterLastInString belongs to a different CharStream.</exception>
        /// <exception cref="OutOfMemoryException">There is not enough memory for the string or the requested string is too large.</exception>
        public string ReadUntil(Iterator iterToCharAfterLastInString) {
            var stream = Stream;
            int idx = Idx;
            if (stream != iterToCharAfterLastInString.Stream)
                throw new ArgumentOutOfRangeException("iterToCharAfterLastInString", "The iterator argument belongs to a different CharStream.");
            if (idx >= 0) {
                if (idx <= iterToCharAfterLastInString.Idx)
                    return stream.String.Substring(idx, iterToCharAfterLastInString.Idx - idx);
                if (iterToCharAfterLastInString.Idx < 0)
                    return stream.String.Substring(idx, stream.IndexEnd - idx);
            }
            return "";
        }

        public override bool Equals(object other) {
            if (!(other is Iterator)) return false;
            return Equals((Iterator) other);
        }

        public bool Equals(Iterator other) {
            return Idx == other.Idx && Stream == other.Stream;
        }

        public override int GetHashCode() {
            return Idx;
        }

        public static bool operator==(Iterator i1, Iterator i2) { return  i1.Equals(i2); }
        public static bool operator!=(Iterator i1, Iterator i2) { return !i1.Equals(i2); }
    }

    /// <summary>Returns a case-folded copy of the string argument. All chars are mapped
    /// using the (non-Turkic) 1-to-1 case folding mappings (v. 5.1) for Unicode code
    /// points in the Basic Multilingual Plane, i.e. code points below 0x10000.
    /// If the argument is null, null is returned.</summary>
    static public string FoldCase(string str) {
        char[] cftable = CaseFoldTable.FoldedChars;
        if (cftable == null) cftable = CaseFoldTable.Initialize();
        if (str != null) {
            for (int i = 0; i < str.Length; ++i) {
                char c   = str[i];
                char cfc = cftable[c];
                if (c != cfc) {
                    StringBuilder sb = new StringBuilder(str);
                    sb[i++] = cfc;
                    for (; i < str.Length; ++i) {
                        c   = str[i];
                        cfc = cftable[c];
                        if (c != cfc) sb[i] = cfc;
                    }
                    return sb.ToString();
                }
            }
        }
        return str;
    }

    /// <summary>Returns the given string with all occurrences of "\r\n" and "\r" replaced
    /// by "\n". If the argument is null, null is returned.</summary>
    static public string NormalizeNewlines(string str) {
        if (str == null || str.Length == 0) return str;
        int nCR   = 0;
        int nCRLF = 0;
        for (int i = 0; i < str.Length; ++i) {
            if (str[i] == '\r') {
                if (i + 1 < str.Length && str[i + 1] == '\n') ++nCRLF;
                else ++nCR;
            }
        }
        if (nCRLF == 0) {
            return nCR == 0 ? str : str.Replace('\r', '\n');
        } else {
            return CopyWithNormalizedNewlines(str, 0, str.Length, nCRLF, nCR);
        }
    }
    static internal string CopyWithNormalizedNewlines(string src, int index, int length, int nCRLF, int nCR) {
        Debug.Assert(length > 0 && nCRLF >= 0 && nCR >= 0 && (nCRLF | nCR) != 0);
        if (nCRLF != 0) {
            StringBuilder sb = new StringBuilder(length - nCRLF);
            int end = index + length;
            int i0 = index;
            if (nCR == 0) {
                int nn = nCRLF;
                int i = index;
                for (;;) {
                    char c = src[i++];
                    if (c == '\r') {
                        sb.Append(src, i0, i - i0 - 1).Append('\n');
                        ++i; // skip over the '\n' in "\r\n"
                        i0 = i;
                        if (--nn == 0) break;
                    }
                }
            } else {
                int nn = nCRLF + nCR;
                int i = index;
                for (;;) {
                    char c = src[i++];
                    if (c == '\r') {
                        sb.Append(src, i0, i - i0 - 1).Append('\n');
                        if (i < end && src[i] == '\n') ++i; // skip over the '\n' in "\r\n"
                        i0 = i;
                        if (--nn == 0) break;
                    }
                }
            }
            if (i0 < end) sb.Append(src, i0, end - i0);
            return sb.ToString();
        } else {
            return new StringBuilder(src, index, length, length).Replace('\r', '\n').ToString();
        }
    }
} // class CharStream

}

#endif
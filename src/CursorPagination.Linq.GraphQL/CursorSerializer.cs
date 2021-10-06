using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace System.Linq.CursorPagination
{
    using System.Linq;

    // this class is not well thought out...
    /// <summary>
    ///     Represents a generic cursor serializer.
    /// </summary>
    public class CursorSerializer : CursorProvider
    {
        /// <summary>
        ///     The current cursor.
        /// </summary>
        protected IReadOnlyDictionary<string, string>? Cursor;

        /// <inheritdoc/>
        protected override bool TryGetValue(string key, Type type, out object? value)
        {
            if (Cursor is null || !Cursor.TryGetValue(key, out var stringValue))
            {
                value = default;
                return false;
            }

            value = DeserializeCursorKey(stringValue, type, key);
            return true;
        }

        /// <summary>
        ///     Set a cursor.
        /// </summary>
        /// <param name="cursor"> The crusor. </param>
        public void SetCursor(string cursor)
        {
            if (cursor is null) throw new ArgumentNullException(nameof(cursor));

            this.Cursor = DeserializeCursor(cursor);
        }

        /// <summary>
        ///     Serialize a cursor.
        /// </summary>
        /// <param name="cursor"> The cursor to serialize. </param>
        /// <returns>
        ///     The serialized cursor.
        /// </returns>
        public virtual string Serialize(ICursor cursor)
        {
            if (cursor is null) throw new ArgumentNullException(nameof(cursor));

            Debug.Assert(cursor.Count > 0);

            string s = string.Join('&', cursor.Values.Select(cursorKey => $"{cursorKey.Key}={SerializeCursorKey(cursorKey.Value, cursorKey.Type, cursorKey.Key)}"));

            return Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
        }

        /// <summary>
        ///     Deserialize the cursor.
        /// </summary>
        /// <param name="cursor"> The cursor to deserialize. </param>
        /// <returns>
        ///     The deserialized cursor.
        /// </returns>
        protected virtual IReadOnlyDictionary<string, string> DeserializeCursor(string cursor)
        {
            // can someone make use of this?
            // well let the user decide what he wants to do overridin this method
            if (string.IsNullOrWhiteSpace(cursor))
            {
                throw new InvalidOperationException($"Invalid empty cursor");
            }

            try
            {
                cursor = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException($"Invalid cursor: '{cursor}'", ex);
            }

            Dictionary<string, string>? keyStringValue = new();

            foreach (string[] parts in cursor.Split('&').Select(p => p.Split('=', 2, StringSplitOptions.None)))
            {
                if (parts.Length != 2)
                {
                    throw new InvalidOperationException($"Invalid cursor: '{cursor}'");
                }

                string key = parts[0];

                if (keyStringValue == null)
                {
                    keyStringValue = new Dictionary<string, string>();
                }

                if (keyStringValue.ContainsKey(key))
                {
                    throw new InvalidOperationException($"Invalid cursor: '{cursor}'");
                }

                keyStringValue.Add(key, parts[1]);
            }

            if (keyStringValue.Count == 0)
            {
                throw new InvalidOperationException($"Invalid cursor: '{cursor}'");
            }

            return keyStringValue;
        }

        /// <summary>
        ///     Deserialize the cursor key value.
        /// </summary>
        /// <param name="stringValue"> The cursor to value as string. </param>
        /// <param name="type"> The cursor type. </param>
        /// <param name="cursorKey"> The cursor key. </param>
        /// <returns>
        ///     The deserialized cursor key value.
        /// </returns>
        protected virtual object? DeserializeCursorKey(string stringValue, Type type, string cursorKey)
        {
            stringValue = Uri.UnescapeDataString(stringValue);

            if (type == typeof(DateTime))
            {
                if (DateTime.TryParseExact(stringValue, "yyyy'-'MM'-'dd'T'HH':'mm':'ss.FFFFFFFK", DateTimeFormatInfo.InvariantInfo, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
                {
                    return date;
                }
                else
                {
                    throw new FormatException($"Could not parse date. Expected ISO-8601 format. Value: {stringValue}");
                }
            }

            TypeConverter? converter = TypeDescriptor.GetConverter(type);
            if (converter != null && converter.CanConvertFrom(typeof(string)))
            {
                return converter.ConvertFrom(stringValue);
            }

            return Convert.ChangeType(stringValue, type);
        }

        /// <summary>
        ///     Serialize a cursor key.
        /// </summary>
        /// <param name="value"> The cursor value. </param>
        /// <param name="type"> The cursor type. </param>
        /// <param name="cursorKey"> The cursor key. </param>
        /// <returns>
        ///     The serialized cusor key.
        /// </returns>
        protected virtual string SerializeCursorKey(object? value, Type type, string cursorKey)
        {
            if (value is DateTime dateTime)
            {
                return dateTime.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss.FFFFFFFK", DateTimeFormatInfo.InvariantInfo);
            }

            string? stringValue;

            TypeConverter converter = TypeDescriptor.GetConverter(type);
            if (converter != null && converter.CanConvertTo(typeof(string)))
            {
                stringValue = (string?)converter.ConvertTo(value, typeof(string));
            }
            else
            {
                stringValue = (string?)Convert.ChangeType(value, typeof(string));
            }

            if (string.IsNullOrEmpty(stringValue))
            {
                return string.Empty;
            }

            StringBuilder stringBuilder = new(stringValue.Length);
            foreach (char item in stringValue)
            {
                if (item is '&' or '=')
                {
                    stringBuilder.Append(Uri.HexEscape(item));
                }
                else
                {
                    stringBuilder.Append(item);
                }
            }

            return stringBuilder.ToString();
        }
    }
}

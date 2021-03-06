﻿using System;
using System.Collections;
using System.Collections.Generic;
#if !SILVERLIGHT
using System.Data;
#endif
using System.Globalization;
using System.IO;
using System.Text;
using System.Collections.Specialized;

namespace fastJSON
{
	internal sealed class JsonSerializer
	{
		static readonly WriteJsonValue[] _convertMethods = RegisterMethods ();

		StringBuilder _output = new StringBuilder ();
		readonly int _MAX_DEPTH = 20;
		int _currentDepth;
		int _before;
		readonly Dictionary<string, int> _globalTypes = new Dictionary<string, int> ();
		readonly Dictionary<object, int> _cirobj = new Dictionary<object, int> ();
		readonly JSONParameters _params;
		readonly bool _useEscapedUnicode = false;
		readonly SerializationManager _manager;

		internal JsonSerializer (JSONParameters param, SerializationManager manager) {
			_params = param;
			_useEscapedUnicode = _params.UseEscapedUnicode;
			_MAX_DEPTH = _params.SerializerMaxDepth;
			_manager = manager;
		}

		internal string ConvertToJSON (object obj, ReflectionCache cache) {
			var m = cache.SerializeMethod;
			if (m != null) {
				m (this, obj);
			}
			else {
				WriteValue (obj);
			}

			if (_params.UseExtensions && _params.UsingGlobalTypes && _globalTypes != null && _globalTypes.Count > 0) {
				var sb = new StringBuilder ();
				sb.Append ("\"" + JsonDict.ExtTypes + "\":{");
				var pendingSeparator = false;
				foreach (var kv in _globalTypes) {
					sb.Append (pendingSeparator ? ",\"" : "\"");
					sb.Append (kv.Key);
					sb.Append ("\":\"");
					sb.Append (Int32ToString (kv.Value));
					sb.Append ('\"');
					pendingSeparator = true;
				}
				sb.Append ("},");
				_output.Insert (_before, sb.ToString ());
			}
			return _output.ToString ();
		}

		private static WriteJsonValue[] RegisterMethods () {
			var r = new WriteJsonValue[Enum.GetNames (typeof (JsonDataType)).Length];
			r[(int)JsonDataType.Array] = WriteArray;
			r[(int)JsonDataType.Bool] = WriteBoolean;
			r[(int)JsonDataType.ByteArray] = WriteByteArray;
			r[(int)JsonDataType.Custom] = WriteCustom;
			r[(int)JsonDataType.DataSet] = WriteDataSet;
			r[(int)JsonDataType.DataTable] = WriteDataTable;
			r[(int)JsonDataType.DateTime] = WriteDateTime;
			r[(int)JsonDataType.Dictionary] = WriteDictionary;
			r[(int)JsonDataType.Double] = WriteDouble;
			r[(int)JsonDataType.Enum] = WriteEnum;
			r[(int)JsonDataType.List] = WriteArray;
			r[(int)JsonDataType.Guid] = WriteGuid;
			r[(int)JsonDataType.Hashtable] = WriteDictionary;
			r[(int)JsonDataType.Int] = WriteInt32;
			r[(int)JsonDataType.Long] = WriteInt64;
			r[(int)JsonDataType.MultiDimensionalArray] = WriteMultiDimensionalArray;
			r[(int)JsonDataType.NameValue] = WriteNameValueCollection;
			r[(int)JsonDataType.Object] = WriteUnknown;
			r[(int)JsonDataType.Single] = WriteSingle;
			r[(int)JsonDataType.String] = WriteString;
			r[(int)JsonDataType.StringDictionary] = WriteStringDictionary;
			r[(int)JsonDataType.StringKeyDictionary] = WriteDictionary;
			r[(int)JsonDataType.TimeSpan] = WriteTimeSpan;
			r[(int)JsonDataType.Undefined] = WriteObject;
			return r;
		}
		private void WriteValue (object obj) {
			if (obj == null || obj is DBNull)
				_output.Append ("null");

			else if (obj is string || obj is char) {
				if (_useEscapedUnicode) {
					WriteStringEscapeUnicode (_output, obj.ToString ());
				}
				else {
					WriteString (_output, obj.ToString ());
				}
			}
			else if (obj is bool)
				_output.Append (((bool)obj) ? "true" : "false"); // conform to standard
			else if (obj is int) {
				_output.Append (Int32ToString ((int)obj));
			}
			else if (
				obj is long || obj is double ||
				obj is decimal || obj is float ||
				obj is byte || obj is short ||
				obj is sbyte || obj is ushort ||
				obj is uint || obj is ulong
			)
				_output.Append (((IConvertible)obj).ToString (NumberFormatInfo.InvariantInfo));

			else if (obj is DateTime)
				WriteDateTime (this, obj);

			else if (obj is Guid)
				WriteGuid (this, obj);

			else if (obj is TimeSpan) {
				WriteTimeSpan (this, obj);
			}
			else if (_params.KVStyleStringDictionary == false && obj is IDictionary &&
				obj.GetType ().IsGenericType && typeof(string).Equals (obj.GetType ().GetGenericArguments ()[0]))

				WriteStringDictionary ((IDictionary)obj);
#if NET_40_OR_GREATER
			else if (_params.KVStyleStringDictionary == false && obj is System.Dynamic.ExpandoObject)
				WriteStringDictionary ((IDictionary<string, object>)obj);
#endif
			else if (obj is IDictionary)
				WriteDictionary ((IDictionary)obj);
#if !SILVERLIGHT
			else if (obj is DataSet)
				WriteDataset ((DataSet)obj);

			else if (obj is DataTable)
				WriteDataTable ((DataTable)obj);
#endif
			else if (obj is byte[])
				WriteBytes ((byte[])obj);

			else if (obj is StringDictionary)
				WriteSD ((StringDictionary)obj);

			else if (obj is NameValueCollection) {
				WriteNameValueCollection ((NameValueCollection)obj);
			}

			else if (obj is IEnumerable)
				WriteArray (this, obj);

			else if (obj is Enum)
				WriteEnum (this, obj);

			else if (_manager.IsTypeRegistered (obj.GetType ()))
				WriteCustom (obj);

			else
				WriteObject (obj);
		}

		private void WriteSD (StringDictionary stringDictionary) {
			_output.Append ('{');

			var pendingSeparator = false;

			foreach (DictionaryEntry entry in stringDictionary) {
				if (_params.SerializeNullValues == false && entry.Value == null) {
				}
				else {
					if (pendingSeparator) _output.Append (',');

					_params.NamingStrategy.WriteName (_output, (string)entry.Key);
					WriteString (_output, (string)entry.Value);
					pendingSeparator = true;
				}
			}
			_output.Append ('}');
		}

		private void WriteCustom (object obj) {
			Serialize s = _manager.GetCustomSerializer (obj.GetType ());
			WriteStringFast (s (obj));
		}

		private void WriteBytes (byte[] bytes) {
#if !SILVERLIGHT
			WriteStringFast (Convert.ToBase64String (bytes, 0, bytes.Length, Base64FormattingOptions.None));
#else
			WriteStringFast(Convert.ToBase64String(bytes, 0, bytes.Length));
#endif
		}

#if !SILVERLIGHT
		private static DatasetSchema GetSchema (DataTable ds) {
			if (ds == null) return null;

			var m = new DatasetSchema
			{
				Info = new List<string> (),
				Name = ds.TableName
			};

			foreach (DataColumn c in ds.Columns) {
				m.Info.Add (ds.TableName);
				m.Info.Add (c.ColumnName);
				m.Info.Add (c.DataType.ToString ());
			}
			// FEATURE : serialize relations and constraints here

			return m;
		}

		private static DatasetSchema GetSchema (DataSet ds) {
			if (ds == null) return null;

			var m = new DatasetSchema
			{
				Info = new List<string> (),
				Name = ds.DataSetName
			};

			foreach (DataTable t in ds.Tables) {
				foreach (DataColumn c in t.Columns) {
					m.Info.Add (t.TableName);
					m.Info.Add (c.ColumnName);
					m.Info.Add (c.DataType.ToString ());
				}
			}
			// FEATURE : serialize relations and constraints here

			return m;
		}

		private static string GetXmlSchema (DataTable dt) {
			using (var writer = new StringWriter ()) {
				dt.WriteXmlSchema (writer);
				return dt.ToString ();
			}
		}

		private void WriteDataset (DataSet ds) {
			_output.Append ('{');
			if (_params.UseExtensions) {
				WritePair (JsonDict.ExtSchema, _params.UseOptimizedDatasetSchema ? (object)GetSchema (ds) : ds.GetXmlSchema ());
				_output.Append (',');
			}
			var tablesep = false;
			foreach (DataTable table in ds.Tables) {
				if (tablesep) _output.Append (',');
				tablesep = true;
				WriteDataTableData (table);
			}
			// end dataset
			_output.Append ('}');
		}

		private void WriteDataTableData (DataTable table) {
			_output.Append ('\"');
			_output.Append (table.TableName);
			_output.Append ("\":[");
			var cols = table.Columns;
			var rowseparator = false;
			foreach (DataRow row in table.Rows) {
				if (rowseparator) _output.Append (',');
				rowseparator = true;
				_output.Append ('[');

				var pendingSeperator = false;
				foreach (DataColumn column in cols) {
					if (pendingSeperator) _output.Append (',');
					WriteValue (row[column]);
					pendingSeperator = true;
				}
				_output.Append (']');
			}

			_output.Append (']');
		}

		void WriteDataTable (DataTable dt) {
			_output.Append ('{');
			if (_params.UseExtensions) {
				WritePair (JsonDict.ExtSchema, _params.UseOptimizedDatasetSchema ? (object)GetSchema (dt) : GetXmlSchema (dt));
				_output.Append (',');
			}

			WriteDataTableData (dt);

			_output.Append ('}');
		}
#endif

		// HACK: This is a very long function, individual parts in regions are inlined for better performance
		private void WriteObject (object obj) {
			#region Detect Circurlar Reference
			var ci = 0;
			if (_cirobj.TryGetValue (obj, out ci) == false)
				_cirobj.Add (obj, _cirobj.Count + 1);
			else {
				if (_currentDepth > 0 && _params.UseExtensions && _params.InlineCircularReferences == false) {
					//_circular = true;
					_output.Append ("{\"" + JsonDict.ExtRefIndex + "\":");
					_output.Append (Int32ToString (ci));
					_output.Append ("}");
					return;
				}
			} 
			#endregion
			var def = _manager.GetReflectionCache (obj.GetType ());
			var si = def.Interceptor;
			if (si != null && si.OnSerializing (obj) == false) {
				return;
			}
			#region Locate Extension Insertion Position
			if (_params.UseExtensions == false || _params.UsingGlobalTypes == false)
				_output.Append ('{');
			else {
				if (_before == 0) {
					_output.Append ('{');
					_before = _output.Length;
				}
				else
					_output.Append ('{');
			}
			#endregion

			_currentDepth++;
			if (_currentDepth > _MAX_DEPTH)
				throw new JsonSerializationException ("Serializer encountered maximum depth of " + _MAX_DEPTH);

			//var map = new Dictionary<string, string> ();
			var append = false;
			#region Write Type Reference
			if (_params.UseExtensions) {
				if (_params.UsingGlobalTypes == false)
					WritePairFast (JsonDict.ExtType, def.AssemblyName);
				else {
					var dt = 0;
					var ct = def.AssemblyName;
					if (_globalTypes.TryGetValue (ct, out dt) == false) {
						dt = _globalTypes.Count + 1;
						_globalTypes.Add (ct, dt);
					}
					WritePairFast (JsonDict.ExtType, Int32ToString (dt));
				}
				append = true;
			} 
			#endregion

			var g = def.Getters;
			var c = g.Length;
			var rp = _params.ShowReadOnlyProperties || _params.EnableAnonymousTypes;
			var rf = _params.ShowReadOnlyFields || _params.EnableAnonymousTypes;
			for (int ii = 0; ii < c; ii++) {
				var p = g[ii];
				#region Skip Members Not For Serialization
				if (p.Serializable == TriState.False) {
					continue;
				}
				if (p.Serializable == TriState.Default) {
					if (p.IsStatic && _params.SerializeStaticMembers == false
						|| p.IsReadOnly && (p.IsProperty && rp == false || p.IsProperty == false && rf == false)) {
						continue;
					}
				} 
				#endregion
				var ji = new JsonItem (p.MemberName, p.Getter (obj), true);
				if (si != null && si.OnSerializing (obj, ji) == false) {
					continue;
				}
				if (p.Converter != null) {
					p.Converter.SerializationConvert (ji);
				}
				#region Convert Items
				if (p.ItemConverter != null && ji._Value is IEnumerable) {
					var ai = new JsonItem (ji.Name, null, false);
					var ol = new List<object> ();
					foreach (var item in (ji._Value as IEnumerable)) {
						ai.Value = item;
						p.ItemConverter.SerializationConvert (ai);
						ol.Add (ai.Value);
					}
					ji._Value = ol;
				}
				#endregion
				#region Determine Serialized Field Name
				if (p.SpecificName) {
					if (ji._Value == null || p.TypedNames == null || p.TypedNames.TryGetValue (ji._Value.GetType (), out ji._Name) == false) {
						ji._Name = p.SerializedName;
					}
				}
				else {
					ji._Name = p.SerializedName;
				}
				#endregion
				#region Skip Null, Default Value or Empty Collection
				if (_params.SerializeNullValues == false && (ji._Value == null || ji._Value is DBNull)) {
					continue;
				}
				if (p.HasDefaultValue && Equals (ji._Value, p.DefaultValue)) {
					// ignore fields with default value
					continue;
				}
				if (p.IsCollection && _params.SerializeEmptyCollections == false && ji._Value is ICollection && (ji._Value as ICollection).Count == 0) {
					continue;
				} 
				#endregion
				if (append)
					_output.Append (',');

				#region Write Name
				if (p.SpecificName) {
					WriteStringFast (ji._Name);
					_output.Append (':');
				}
				else {
					_params.NamingStrategy.WriteName (_output, ji._Name);
				}
				#endregion
				#region Write Value
				if (p.WriteValue != null && p.Converter == null) {
					var v = ji._Value;
					if (v == null || v is DBNull) {
						_output.Append ("null");
					}
					else {
						p.WriteValue (this, v);
					}
				}
				else {
					WriteValue (ji._Value);
				}
				#endregion

				// TODO: Candidate to removal of unknown use of map
				//if (ji._Value != null && _params.UseExtensions) {
				//	var tt = ji._Value.GetType ();
				//	if (typeof (object).Equals (tt))
				//		map.Add (p.SerializedName, tt.ToString ());
				//}
				append = true;
			}
			//if (map.Count > 0 && _params.UseExtensions) {
			//	_output.Append (",\"" + JsonDict.ExtMap + "\":");
			//	WriteStringDictionary (map);
			//	append = true;
			//}
			if (si != null) {
				#region Write Extra Values
				var ev = si.SerializeExtraValues (obj);
				if (ev != null) {
					foreach (var item in ev) {
						if (append)
							_output.Append (',');
						WritePair (item._Name, item._Value);
						append = true;
					}
				}
				#endregion
				si.OnSerialized (obj);
			}
		_currentDepth--;
		_output.Append ('}');
	}


	private void WritePairFast (string name, string value) {
			WriteStringFast (name);
			_output.Append (':');
			WriteStringFast (value);
		}

		private void WritePair (string name, object value) {
			WriteStringFast (name);
			_output.Append (':');
			WriteValue (value);
		}

		static void WriteArray (JsonSerializer serializer, object value) {
			IEnumerable array = value as IEnumerable;
			var o = serializer._output;
			if (array == null) {
				o.Append ("null");
				return;
			}
			//if (_params.SerializeEmptyCollections == false) {
			//	var c = array as ICollection;
			//	if (c.Count == 0) {
			//		return;
			//	}
			//}

			var list = array as IList;
			if (list != null) {
				var c = list.Count;
				if (c == 0) {
					o.Append ("[]");
					return;
				}

				var t = list.GetType ();
				if (t.IsArray && t.GetArrayRank () >  1) {
					WriteMultiDimensionalArray (serializer, list);
					return;
				}
				var d = serializer._manager.GetReflectionCache (t);
				var w = d.ItemSerializer;
				if (w != null) {
					o.Append ('[');
					var v = list[0];
					if (v == null) {
						o.Append ("null");
					}
					else {
						w (serializer, v);
					}
					for (int i = 1; i < c; i++) {
						o.Append (',');
						v = list[i];
						if (v == null) {
							o.Append ("null");
						}
						else {
							w (serializer, v);
						}
					}
					o.Append (']');
					return;
				}

				o.Append ('[');
				serializer.WriteValue (list[0]);
				for (int i = 1; i < c; i++) {
					o.Append (',');
					serializer.WriteValue (list[i]);
				}
				o.Append (']');
				return;
			}

			var pendingSeperator = false;
			o.Append ('[');
			foreach (object obj in array) {
				if (pendingSeperator) o.Append (',');

				serializer.WriteValue (obj);

				pendingSeperator = true;
			}
			o.Append (']');
		}

		static void WriteMultiDimensionalArray (JsonSerializer serializer, object value) {
			var a = value as Array;
			if (a == null) {
				serializer._output.Append ("null");
				return;
			}
			var m = serializer._manager.GetReflectionCache (a.GetType ().GetElementType ()).SerializeMethod;
			serializer.WriteMultiDimensionalArray (m, a);
		}

		private void WriteMultiDimensionalArray (WriteJsonValue m, Array md) {
			var r = md.Rank;
			var lb = new int[r];
			var ub = new int[r];
			var mdi = new int[r];
			for (int i = 0; i < r; i++) {
				lb[i] = md.GetLowerBound (i);
				ub[i] = md.GetUpperBound (i) + 1;
			}
			Array.Copy (lb, 0, mdi, 0, r);
			WriteMultiDimensionalArray (m, md, r, lb, ub, mdi, 0);
		}

		private void WriteMultiDimensionalArray (WriteJsonValue m, Array md, int r, int[] lb, int[] ub, int[] mdi, int ri) {
			var u = ub[ri];
			if (ri < r - 1) {
				_output.Append ('[');
				bool s = false;
				var d = ri;
				do {
					if (s) {
						_output.Append (',');
					}
					Array.Copy (lb, d + 1, mdi, d + 1, r - d - 1);
					WriteMultiDimensionalArray (m, md, r, lb, ub, mdi, ++d);
					d = ri;
					s = true;
				} while (++mdi[ri] < u);
				_output.Append (']');
			}
			else if (ri == r - 1) {
				_output.Append ('[');
				bool s = false;
				do {
					if (s) {
						_output.Append (',');
					}
					var v = md.GetValue (mdi);
					if (v == null || v is DBNull) {
						_output.Append ("null");
					}
					else {
						m (this, v);
					}
					s = true;
				} while (++mdi[ri] < u);
				_output.Append (']');
			}
		}

		private void WriteStringDictionary (IDictionary dic) {
			_output.Append ('{');
			var pendingSeparator = false;
			foreach (DictionaryEntry entry in dic) {
				if (_params.SerializeNullValues == false && entry.Value == null) {
					continue;
				}
				if (pendingSeparator) _output.Append (',');
				_params.NamingStrategy.WriteName (_output, (string)entry.Key);
				WriteValue (entry.Value);
				pendingSeparator = true;
			}
			_output.Append ('}');
		}

		private void WriteNameValueCollection (NameValueCollection collection) {
			_output.Append ('{');
			var pendingSeparator = false;
			var length = collection.Count;
			for (int i = 0; i < length; i++) {
				var v = collection.GetValues (i);
				if (v == null && _params.SerializeNullValues == false) {
					continue;
				}
				if (pendingSeparator) _output.Append (',');
				pendingSeparator = true;
				_params.NamingStrategy.WriteName (_output, collection.GetKey (i));
				if (v == null) {
					_output.Append ("null");
					continue;
				}
				var vl = v.Length;
				if (vl == 0) {
					_output.Append ("\"\"");
					continue;
				}
				if (vl == 1) {
					if (_useEscapedUnicode) {
						WriteStringEscapeUnicode (_output, v[0]);
					}
					else {
						WriteString (_output, v[0]);
					}
				}
				else {
					_output.Append ('[');
					if (_useEscapedUnicode) {
						WriteStringEscapeUnicode (_output, v[0]);
					}
					else {
						WriteString (_output, v[0]);
					}
					for (int vi = 1; vi < vl; vi++) {
						_output.Append (',');
						if (_useEscapedUnicode) {
							WriteStringEscapeUnicode (_output, v[vi]);
						}
						else {
							WriteString (_output, v[vi]);
						}
					}
					_output.Append (']');
				}
			}
			_output.Append ('}');
		}

		private void WriteStringDictionary (IDictionary<string, object> dic) {
			_output.Append ('{');
			var pendingSeparator = false;
			foreach (KeyValuePair<string, object> entry in dic) {
				if (_params.SerializeNullValues == false && entry.Value == null) {
					continue;
				}
				if (pendingSeparator) _output.Append (',');
				_params.NamingStrategy.WriteName (_output, entry.Key);
				WriteValue (entry.Value);
				pendingSeparator = true;
			}
			_output.Append ('}');
		}

		private void WriteDictionary (IDictionary dic) {
			_output.Append ('[');

			var pendingSeparator = false;

			foreach (DictionaryEntry entry in dic) {
				if (pendingSeparator) _output.Append (',');
				_output.Append ('{');
				WritePair ("k", entry.Key);
				_output.Append (",");
				WritePair ("v", entry.Value);
				_output.Append ('}');

				pendingSeparator = true;
			}
			_output.Append (']');
		}

		private void WriteStringFast (string s) {
			_output.Append ('\"');
			_output.Append (s);
			_output.Append ('\"');
		}

		internal static void WriteStringEscapeUnicode (StringBuilder output, string s) {
			output.Append ('\"');

			var runIndex = -1;
			var l = s.Length;
			for (var index = 0; index < l; ++index) {
				var c = s[index];
				if (c >= ' ' && c < 128 && c != '\"' && c != '\\') {
					if (runIndex == -1)
						runIndex = index;

					continue;
				}

				if (runIndex != -1) {
					output.Append (s, runIndex, index - runIndex);
					runIndex = -1;
				}

				switch (c) {
					case '\t': output.Append ("\\t"); break;
					case '\r': output.Append ("\\r"); break;
					case '\n': output.Append ("\\n"); break;
					case '"':
					case '\\': output.Append ('\\'); output.Append (c); break;
					default:
						output.Append ("\\u");
						// hard-code this line to improve performance:
						// output.Append (((int)c).ToString ("X4", NumberFormatInfo.InvariantInfo));
						var n = (c >> 12) & 0x0F;
						output.Append ((char)(n > 9 ? n + ('A' - 10) : n + '0'));
						n = (c >> 8) & 0x0F;
						output.Append ((char)(n > 9 ? n + ('A' - 10) : n + '0'));
						n = (c >> 4) & 0x0F;
						output.Append ((char)(n > 9 ? n + ('A' - 10) : n + '0'));
						n = c & 0x0F;
						output.Append ((char)(n > 9 ? n + ('A' - 10) : n + '0'));
						break;
				}
			}

			if (runIndex != -1)
				output.Append (s, runIndex, s.Length - runIndex);

			output.Append ('\"');
		}

		internal static void WriteString (StringBuilder output, string s) {
			output.Append ('\"');

			var runIndex = -1;
			var l = s.Length;
			for (var index = 0; index < l; ++index) {
				var c = s[index];
				if (c != '\t' && c != '\n' && c != '\r' && c != '\"' && c != '\\')// && c != ':' && c!=',')
				{
					if (runIndex == -1)
						runIndex = index;

					continue;
				}

				if (runIndex != -1) {
					output.Append (s, runIndex, index - runIndex);
					runIndex = -1;
				}

				switch (c) {
					case '\t': output.Append ("\\t"); break;
					case '\r': output.Append ("\\r"); break;
					case '\n': output.Append ("\\n"); break;
					case '"':
					case '\\': output.Append ('\\'); output.Append (c); break;
					default:
						output.Append (c);
						break;
				}
			}

			if (runIndex != -1)
				output.Append (s, runIndex, s.Length - runIndex);

			output.Append ('\"');
		}

		static string ToFixedWidthString (int value, int digits) {
			var chs = new char[digits];
			for (int i = chs.Length - 1; i >= 0; i--) {
				chs[i] = (char)('0' + (value % 10));
				value /= 10;
			}
			return new string (chs);
		}

		static string Int64ToString (long value) {
			var n = false;
			var d = 20;
			if (value < 0) {
				if (value == Int64.MinValue) {
					return "-9223372036854775808";
				}
				n = true;
				value = -value;
			}
			if (value < 10L) {
				d = 2;
			}
			else if (value < 1000L) {
				d = 4;
			}
			else if (value < 1000000L) {
				d = 7;
			}
			var chs = new char[d];
			var i = d;
			while (--i > 0) {
				chs[i] = (char)('0' + (value % 10L));
				value /= 10L;
				if (value == 0L) {
					break;
				}
			}
			if (n) {
				chs[--i] = '-';
			}
			return new string (chs, i, d - i);
		}
		static string UInt64ToString (ulong value) {
			var d = 20;
			if (value < 10UL) {
				d = 2;
			}
			else if (value < 1000UL) {
				d = 4;
			}
			else if (value < 1000000UL) {
				d = 7;
			}
			var chs = new char[d];
			var i = d;
			while (--i > 0) {
				chs[i] = (char)('0' + (value % 10UL));
				value /= 10UL;
				if (value == 0UL) {
					break;
				}
			}
			return new string (chs, i, d - i);
		}
		static string Int32ToString (int value) {
			var n = false;
			var d = 11;
			if (value < 0) {
				if (value == Int32.MinValue) {
					return "-2147483648";
				}
				n = true;
				value = -value;
			}
			if (value < 10) {
				d = 2;
			}
			else if (value < 1000) {
				d = 4;
			}
			var chs = new char[d];
			var i = d;
			while (--i > 0) {
				chs[i] = (char)('0' + (value % 10));
				value /= 10;
				if (value == 0) {
					break;
				}
			}
			if (n) {
				chs[--i] = '-';
			}
			return new string (chs, i, d - i);
		}

		#region WriteJsonValue delegate methods
		internal static WriteJsonValue GetWriteJsonMethod (Type type) {
			var t = Reflection.GetJsonDataType (type);
			if (t != JsonDataType.Primitive) {
				return _convertMethods[(int)t];
			}
			return typeof(int).Equals (type) ? WriteInt32
					: typeof(long).Equals (type) ? WriteInt64
					: typeof(string).Equals (type) ? WriteString
					: typeof(double).Equals (type) ? WriteDouble
					: typeof(float).Equals (type) ? WriteSingle
					: typeof(decimal).Equals (type) ? WriteDecimal
					: typeof(bool).Equals (type) ? WriteBoolean
					: typeof(byte).Equals (type) ? WriteByte
					: typeof(DateTime).Equals (type) ? WriteDateTime
					: typeof(TimeSpan).Equals (type) ? WriteTimeSpan
					: typeof(Guid).Equals (type) ? WriteGuid
					: typeof(sbyte).Equals (type) ? WriteSByte
					: typeof(short).Equals (type) ? WriteInt16
					: typeof(ushort).Equals (type) ? WriteUInt16
					: typeof(uint).Equals (type) ? WriteUInt32
					: typeof(ulong).Equals (type) ? WriteUInt64
					: typeof(char).Equals (type) ? WriteChar
					: type.IsSubclassOf (typeof(Enum)) ? WriteEnum
					: type.IsSubclassOf (typeof(Array)) && type.GetArrayRank () > 1 ? WriteMultiDimensionalArray
					: type.IsSubclassOf (typeof(Array)) && typeof(byte[]).Equals (type) == false ? WriteArray
					: (WriteJsonValue)WriteUnknown;
		}

		static void WriteByte (JsonSerializer serializer, object value) {
			serializer._output.Append (Int32ToString ((byte)value));
		}
		static void WriteSByte (JsonSerializer serializer, object value) {
			serializer._output.Append (Int32ToString ((sbyte)value));
		}
		static void WriteInt16 (JsonSerializer serializer, object value) {
			serializer._output.Append (Int32ToString ((short)value));
		}
		static void WriteUInt16 (JsonSerializer serializer, object value) {
			serializer._output.Append (Int32ToString ((ushort)value));
		}
		static void WriteInt32 (JsonSerializer serializer, object value) {
			serializer._output.Append (Int32ToString ((int)value));
		}
		static void WriteUInt32 (JsonSerializer serializer, object value) {
			serializer._output.Append (Int64ToString ((uint)value));
		}
		static void WriteInt64 (JsonSerializer serializer, object value) {
			serializer._output.Append (Int64ToString ((long)value));
		}
		static void WriteUInt64 (JsonSerializer serializer, object value) {
			serializer._output.Append (UInt64ToString ((ulong)value));
		}
		static void WriteSingle (JsonSerializer serializer, object value) {
			serializer._output.Append (((float)value).ToString (NumberFormatInfo.InvariantInfo));
		}
		static void WriteDouble (JsonSerializer serializer, object value) {
			serializer._output.Append (((double)value).ToString (NumberFormatInfo.InvariantInfo));
		}
		static void WriteDecimal (JsonSerializer serializer, object value) {
			serializer._output.Append (((decimal)value).ToString (NumberFormatInfo.InvariantInfo));
		}
		static void WriteBoolean (JsonSerializer serializer, object value) {
			serializer._output.Append ((bool)value ? "true" : "false");
		}
		static void WriteChar (JsonSerializer serializer, object value) {
			WriteString (serializer, ((char)value).ToString ());
		}

		static void WriteDateTime (JsonSerializer serializer, object value) {
			// datetime format standard : yyyy-MM-dd HH:mm:ss
			var dt = (DateTime)value;
			var parameter = serializer._params;
			var output = serializer._output;
			if (parameter.UseUTCDateTime)
				dt = dt.ToUniversalTime ();

			output.Append ('\"');
			output.Append (ToFixedWidthString (dt.Year, 4));
			output.Append ('-');
			output.Append (ToFixedWidthString (dt.Month, 2));
			output.Append ('-');
			output.Append (ToFixedWidthString (dt.Day, 2));
			output.Append ('T'); // strict ISO date compliance
			output.Append (ToFixedWidthString (dt.Hour, 2));
			output.Append (':');
			output.Append (ToFixedWidthString (dt.Minute, 2));
			output.Append (':');
			output.Append (ToFixedWidthString (dt.Second, 2));
			if (parameter.DateTimeMilliseconds) {
				output.Append ('.');
				output.Append (ToFixedWidthString (dt.Millisecond, 3));
			}
			if (parameter.UseUTCDateTime)
				output.Append ('Z');

			output.Append ('\"');
		}

		static void WriteTimeSpan (JsonSerializer serializer, object timeSpan) {
			serializer.WriteStringFast ((((TimeSpan)timeSpan).ToString ()));
		}

		static void WriteString (JsonSerializer serializer, object value) {
			if (value == null) {
				serializer._output.Append ("null");
				return;
			}
			var s = (string)value;
			if (s.Length == 0) {
				serializer._output.Append ("\"\"");
				return;
			}
			if (serializer._params.UseEscapedUnicode) {
				WriteStringEscapeUnicode (serializer._output, s);
			}
			else {
				WriteString (serializer._output, s);
			}
		}

		static void WriteGuid (JsonSerializer serializer, object guid) {
			if (serializer._params.UseFastGuid == false)
				serializer.WriteStringFast (((Guid)guid).ToString ());
			else
				serializer.WriteBytes (((Guid)guid).ToByteArray ());
		}

		static void WriteEnum (JsonSerializer serializer, object value) {
			Enum e = (Enum)value;
			// TODO : optimize enum write
			if (serializer._params.UseValuesOfEnums) {
				serializer._output.Append (Convert.ToInt64 (e).ToString (NumberFormatInfo.InvariantInfo));
				return;
			}
			var n = serializer._manager.GetEnumName (e);
			if (n != null) {
				serializer.WriteStringFast (n);
			}
			else {
				serializer._output.Append (Convert.ToInt64 (e).ToString (NumberFormatInfo.InvariantInfo));
			}
		}
		static void WriteByteArray(JsonSerializer serializer, object value) {
			serializer.WriteStringFast (Convert.ToBase64String ((byte[])value));
		}
		static void WriteCustom (JsonSerializer serializer, object value) {
			serializer.WriteCustom (value);
		}
		static void WriteDataSet (JsonSerializer serializer, object value) {
			serializer.WriteDataset ((DataSet)value);
		}
		static void WriteDataTable (JsonSerializer serializer, object value) {
			serializer.WriteDataTable ((DataTable)value);
		}
		static void WriteDictionary (JsonSerializer serializer, object value) {
			if (serializer._params.KVStyleStringDictionary == false && value is IDictionary &&
				value.GetType ().IsGenericType && typeof (string).Equals (value.GetType ().GetGenericArguments ()[0]))

				serializer.WriteStringDictionary ((IDictionary)value);
#if NET_40_OR_GREATER
			else if (serializer._params.KVStyleStringDictionary == false && value is System.Dynamic.ExpandoObject)
				serializer.WriteStringDictionary ((IDictionary<string, object>)value);
#endif
			else if (value is IDictionary)
				serializer.WriteDictionary ((IDictionary)value);
		}
		static void WriteStringDictionary (JsonSerializer serializer, object value) {
			serializer.WriteSD ((StringDictionary)value);
		}
		static void WriteNameValueCollection (JsonSerializer serializer, object value) {
			serializer.WriteNameValueCollection ((NameValueCollection)value);
		}
		static void WriteObject(JsonSerializer serializer, object value) {
			serializer.WriteObject (value);
		}
		static void WriteUnknown (JsonSerializer serializer, object value) {
			serializer.WriteValue (value);
		}
		#endregion
	}
}

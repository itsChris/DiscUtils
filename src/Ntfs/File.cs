//
// Copyright (c) 2008-2009, Kenneth Bell
//
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
//

using System;
using System.Collections.Generic;
using System.IO;

namespace DiscUtils.Ntfs
{
    internal class File
    {
        protected INtfsContext _context;

        private MasterFileTable _mft;
        private FileRecord _baseRecord;
        private ObjectCache<ushort, NtfsAttribute> _attributeCache;
        private ObjectCache<string, Index> _indexCache;

        private bool _baseRecordDirty;

        public File(INtfsContext context, FileRecord baseRecord)
        {
            _context = context;
            _mft = _context.Mft;
            _baseRecord = baseRecord;
            _attributeCache = new ObjectCache<ushort, NtfsAttribute>();
            _indexCache = new ObjectCache<string, Index>();
        }

        public uint IndexInMft
        {
            get { return _baseRecord.MasterFileTableIndex; }
        }

        public uint MaxMftRecordSize
        {
            get { return _baseRecord.AllocatedSize; }
        }

        public FileReference MftReference
        {
            get { return new FileReference(_baseRecord.MasterFileTableIndex, _baseRecord.SequenceNumber); }
        }

        public ushort UpdateSequenceNumber
        {
            get { return _baseRecord.UpdateSequenceNumber; }
        }

        public int MftRecordFreeSpace
        {
            get { return _mft.RecordSize - _baseRecord.Size; }
        }

        public string CanonicalName
        {
            get
            {
                if (IndexInMft == MasterFileTable.RootDirIndex)
                {
                    return "";
                }
                else
                {
                    FileNameRecord firstName = GetAttributeContent<FileNameRecord>(AttributeType.FileName);
                    return Path.Combine(_context.GetDirectoryByRef(firstName.ParentDirectory).CanonicalName, firstName.FileName);
                }
            }
        }

        public List<string> Names
        {
            get
            {
                List<string> result = new List<string>();

                if (IndexInMft == MasterFileTable.RootDirIndex)
                {
                    result.Add("");
                }
                else
                {
                    foreach (StructuredNtfsAttribute<FileNameRecord> attr in GetAttributes(AttributeType.FileName))
                    {
                        string name = attr.Content.FileName;

                        foreach (string dirName in _context.GetDirectoryByRef(attr.Content.ParentDirectory).Names)
                        {
                            result.Add(Path.Combine(dirName, name));
                        }
                    }
                }

                return result;
            }
        }

        public void Modified()
        {
            DateTime now = DateTime.UtcNow;
            StructuredNtfsAttribute<StandardInformation> attr = (StructuredNtfsAttribute<StandardInformation>)GetAttribute(AttributeType.StandardInformation);
            attr.Content.LastAccessTime = now;
            attr.Content.ModificationTime = now;
            attr.Save();
            MarkMftRecordDirty();
        }

        public void Accessed()
        {
            StructuredNtfsAttribute<StandardInformation> attr = (StructuredNtfsAttribute<StandardInformation>)GetAttribute(AttributeType.StandardInformation);
            attr.Content.LastAccessTime = DateTime.UtcNow;
            attr.Save();
            MarkMftRecordDirty();
        }

        public void MarkMftRecordDirty()
        {
            _baseRecordDirty = true;
        }

        public bool MftRecordIsDirty
        {
            get
            {
                return _baseRecordDirty;
            }
        }

        public void UpdateRecordInMft()
        {
            if(_baseRecordDirty)
            {
                if (NtfsTransaction.Current != null)
                {
                    StructuredNtfsAttribute<StandardInformation> saAttr = (StructuredNtfsAttribute<StandardInformation>)GetAttribute(AttributeType.StandardInformation);
                    saAttr.Content.MftChangedTime = NtfsTransaction.Current.Timestamp;
                    saAttr.Save();
                }

                // Make attributes non-resident until the data in the record fits, start with DATA attributes
                // by default, then kick other 'can-be' attributes out, finally move indexes.
                bool fixedAttribute = true;
                while (_baseRecord.Size > _mft.RecordSize && fixedAttribute)
                {
                    fixedAttribute = false;
                    foreach (var attr in _baseRecord.Attributes)
                    {
                        if (!attr.IsNonResident && attr.AttributeType == AttributeType.Data)
                        {
                            MakeAttributeNonResident(attr.AttributeId, (int)attr.DataLength);
                            fixedAttribute = true;
                            break;
                        }
                    }

                    if (!fixedAttribute)
                    {
                        foreach (var attr in _baseRecord.Attributes)
                        {
                            if (!attr.IsNonResident && _context.AttributeDefinitions.CanBeNonResident(attr.AttributeType))
                            {
                                MakeAttributeNonResident(attr.AttributeId, (int)attr.DataLength);
                                fixedAttribute = true;
                                break;
                            }
                        }
                    }

                    if (!fixedAttribute)
                    {
                        foreach (var attr in _baseRecord.Attributes)
                        {
                            if (attr.AttributeType == AttributeType.IndexRoot
                                && ShrinkIndexRoot(attr.Name))
                            {
                                fixedAttribute = true;
                                break;
                            }
                        }
                    }
                }

                // Still too large?  Error.
                if (_baseRecord.Size > _mft.RecordSize)
                {
                    throw new NotSupportedException("Spanning over multiple FileRecord entries - TBD");
                }

                _baseRecordDirty = false;
                _mft.WriteRecord(_baseRecord);
            }
        }

        private bool ShrinkIndexRoot(string indexName)
        {
            NtfsAttribute attr = GetAttribute(AttributeType.IndexRoot, indexName);

            // Nothing to win, can't make IndexRoot smaller than this
            // 8 = min size of entry that points to IndexAllocation...
            if (attr.Length <= IndexRoot.HeaderOffset + IndexHeader.Size + 8)
            {
                return false;
            }

            Index idx = GetIndex(indexName);
            return idx.ShrinkRoot();
        }

        public ushort HardLinkCount
        {
            get { return _baseRecord.HardLinkCount; }
            set { _baseRecord.HardLinkCount = value; }
        }

        public Index CreateIndex(string name, AttributeType attrType, AttributeCollationRule collRule)
        {
            Index.Create(attrType, collRule, this, name);
            return GetIndex(name);
        }

        public Index GetIndex(string name)
        {
            Index idx = _indexCache[name];

            if (idx == null)
            {
                idx = new Index(this, name, _context.BiosParameterBlock, _context.UpperCase);
                _indexCache[name] = idx;
            }

            return idx;
        }

        /// <summary>
        /// Creates a new unnamed attribute.
        /// </summary>
        /// <param name="type">The type of the new attribute</param>
        public ushort CreateAttribute(AttributeType type)
        {
            bool indexed = _context.AttributeDefinitions.IsIndexed(type);
            ushort id = _baseRecord.CreateAttribute(type, null, indexed);
            MarkMftRecordDirty();
            return id;
        }

        /// <summary>
        /// Creates a new attribute.
        /// </summary>
        /// <param name="type">The type of the new attribute</param>
        /// <param name="name">The name of the new attribute</param>
        public ushort CreateAttribute(AttributeType type, string name)
        {
            bool indexed = _context.AttributeDefinitions.IsIndexed(type);
            ushort id = _baseRecord.CreateAttribute(type, name, indexed);
            MarkMftRecordDirty();
            return id;
        }

        public void RemoveAttribute(ushort id)
        {
            NtfsAttribute attr = GetAttribute(id);
            if (attr != null)
            {
                if (attr.Record.AttributeType == AttributeType.IndexRoot)
                {
                    _indexCache.Remove(attr.Record.Name);
                }

                using (Stream s = new FileAttributeStream(this, id, FileAccess.Write))
                {
                    s.SetLength(0);
                }

                _baseRecord.RemoveAttribute(id);
                _attributeCache.Remove(id);
            }
        }

        /// <summary>
        /// Gets an attribute by it's id.
        /// </summary>
        /// <param name="id">The id of the attribute</param>
        /// <returns>The attribute</returns>
        public NtfsAttribute GetAttribute(ushort id)
        {
            foreach (var attr in AllAttributeRecords)
            {
                if (attr.AttributeId == id)
                {
                    return InnerGetAttribute(attr);
                }
            }
            return null;
        }

        /// <summary>
        /// Gets the first (if more than one) instance of an unnamed attribute.
        /// </summary>
        /// <param name="type">The attribute type</param>
        /// <returns>The attribute, or <c>null</c>.</returns>
        public NtfsAttribute GetAttribute(AttributeType type)
        {
            return GetAttribute(type, null);
        }

        /// <summary>
        /// Gets the content of an attribute.
        /// </summary>
        /// <typeparam name="T">The attribute's content structure</typeparam>
        /// <param name="id">The attribute's id</param>
        /// <returns>The attribute, or <c>null</c>.</returns>
        public T GetAttributeContent<T>(ushort id)
            where T : IByteArraySerializable, IDiagnosticTraceable, new()
        {
            return new StructuredNtfsAttribute<T>(this, GetAttribute(id).Record).Content;
        }

        /// <summary>
        /// Gets the content of the first (if more than one) instance of an unnamed attribute.
        /// </summary>
        /// <typeparam name="T">The attribute's content structure</typeparam>
        /// <param name="type">The attribute type</param>
        /// <returns>The attribute, or <c>null</c>.</returns>
        public T GetAttributeContent<T>(AttributeType type)
            where T : IByteArraySerializable, IDiagnosticTraceable, new()
        {
            return new StructuredNtfsAttribute<T>(this, GetAttribute(type).Record).Content;
        }

        /// <summary>
        /// Gets the content of the first (if more than one) instance of an unnamed attribute.
        /// </summary>
        /// <typeparam name="T">The attribute's content structure</typeparam>
        /// <param name="type">The attribute type</param>
        /// <param name="name">The attribute's name</param>
        /// <returns>The attribute, or <c>null</c>.</returns>
        public T GetAttributeContent<T>(AttributeType type, string name)
            where T : IByteArraySerializable, IDiagnosticTraceable, new()
        {
            byte[] buffer;
            using (Stream s = GetAttribute(type, name).OpenRaw(FileAccess.Read))
            {
                buffer = Utilities.ReadFully(s, (int)s.Length);
            }

            T value = new T();
            value.ReadFrom(buffer, 0);
            return value;
        }

        /// <summary>
        /// Sets the content of an attribute.
        /// </summary>
        /// <typeparam name="T">The attribute's content structure</typeparam>
        /// <param name="id">The attribute's id</param>
        /// <param name="value">The new value for the attribute</param>
        public void SetAttributeContent<T>(ushort id, T value)
            where T : IByteArraySerializable, IDiagnosticTraceable, new()
        {
            byte[] buffer = new byte[value.Size];
            value.WriteTo(buffer, 0);
            using (Stream s = GetAttribute(id).OpenRaw(FileAccess.Write))
            {
                s.Write(buffer, 0, buffer.Length);
                s.SetLength(buffer.Length);
            }
        }

        /// <summary>
        ///  Gets the first (if more than one) instance of a named attribute.
        /// </summary>
        /// <param name="type">The attribute type</param>
        /// <param name="name">The attribute's name</param>
        /// <returns>The attribute of <c>null</c>.</returns>
        public NtfsAttribute GetAttribute(AttributeType type, string name)
        {
            foreach (var attr in AllAttributeRecords)
            {
                if (attr.AttributeType == type && attr.Name == name)
                {
                    return InnerGetAttribute(attr);
                }
            }
            return null;
        }

        /// <summary>
        /// Enumerates through all attributes.
        /// </summary>
        public IEnumerable<NtfsAttribute> AllAttributes
        {
            get
            {
                foreach (var attr in AllAttributeRecords)
                {
                    yield return InnerGetAttribute(attr);
                }
            }
        }

        /// <summary>
        ///  Gets all instances of an unnamed attribute.
        /// </summary>
        /// <param name="type">The attribute type</param>
        /// <returns>The attribute, or <c>null</c>.</returns>
        public NtfsAttribute[] GetAttributes(AttributeType type)
        {
            List<NtfsAttribute> matches = new List<NtfsAttribute>();

            foreach (var attr in AllAttributeRecords)
            {
                if (attr.AttributeType == type && string.IsNullOrEmpty(attr.Name))
                {
                    matches.Add(InnerGetAttribute(attr));
                }
            }

            return matches.ToArray();
        }

        public SparseStream OpenAttribute(ushort id, FileAccess access)
        {
            NtfsAttribute attr = GetAttribute(id);

            if (attr == null)
            {
                throw new IOException("No such attribute: " + id);
            }

            return new FileAttributeStream(this, id, access);
        }

        public SparseStream OpenAttribute(AttributeType type, FileAccess access)
        {
            NtfsAttribute attr = GetAttribute(type);

            if (attr == null)
            {
                throw new IOException("No such attribute: " + type);
            }

            return new FileAttributeStream(this, attr.Id, access);
        }

        public SparseStream OpenAttribute(AttributeType type, string name, FileAccess access)
        {
            NtfsAttribute attr = GetAttribute(type, name);

            if (attr == null)
            {
                throw new IOException("No such attribute: " + type + "(" + name + ")");
            }

            return new FileAttributeStream(this, attr.Id, access);
        }

        public void MakeAttributeNonResident(ushort attrId, int maxData)
        {
            NtfsAttribute attr = GetAttribute(attrId);

            if(attr.IsNonResident)
            {
                throw new InvalidOperationException("Attribute is already non-resident");
            }

            attr.SetNonResident(true, maxData);
            _baseRecord.SetAttribute(attr.Record);
        }

        internal void MakeAttributeResident(ushort attrId, int maxData)
        {
            NtfsAttribute attr = GetAttribute(attrId);

            if (!attr.IsNonResident)
            {
                throw new InvalidOperationException("Attribute is already resident");
            }

            attr.SetNonResident(false, maxData);
            _baseRecord.SetAttribute(attr.Record);
        }

        public FileAttributes FileAttributes
        {
            get
            {
                return GetAttributeContent<FileNameRecord>(AttributeType.FileName).FileAttributes;
            }
        }

        public FileNameRecord GetFileNameRecord(string name, bool freshened)
        {
            NtfsAttribute[] attrs = GetAttributes(AttributeType.FileName);
            StructuredNtfsAttribute<FileNameRecord> attr = null;
            if (String.IsNullOrEmpty(name))
            {
                if (attrs.Length != 0)
                {
                    attr = (StructuredNtfsAttribute<FileNameRecord>)attrs[0];
                }
            }
            else
            {
                foreach (StructuredNtfsAttribute<FileNameRecord> a in attrs)
                {
                    if (_context.UpperCase.Compare(a.Content.FileName, name) == 0)
                    {
                        attr = a;
                    }
                }
                if (attr == null)
                {
                    throw new FileNotFoundException("File name not found on file", name);
                }
            }

            FileNameRecord fnr = (attr == null ? new FileNameRecord() : new FileNameRecord(attr.Content));

            if (freshened)
            {
                FreshenFileName(fnr, false);
            }

            return fnr;
        }

        public void FreshenFileName(FileNameRecord fileName, bool updateMftRecord)
        {
            //
            // Freshen the record from the definitive info in the other attributes
            //
            StandardInformation si = GetAttributeContent<StandardInformation>(AttributeType.StandardInformation);
            NtfsAttribute anonDataAttr = GetAttribute(AttributeType.Data);

            fileName.CreationTime = si.CreationTime;
            fileName.ModificationTime = si.ModificationTime;
            fileName.MftChangedTime = si.MftChangedTime;
            fileName.LastAccessTime = si.LastAccessTime;
            fileName.Flags = si.FileAttributes;

            if (_baseRecordDirty && NtfsTransaction.Current != null)
            {
                fileName.MftChangedTime = NtfsTransaction.Current.Timestamp;
            }

            // Directories don't have directory flag set in StandardInformation, so set from MFT record
            if ((_baseRecord.Flags & FileRecordFlags.IsDirectory) != 0)
            {
                fileName.Flags |= FileAttributeFlags.Directory;
            }

            if (anonDataAttr != null)
            {
                fileName.RealSize = (ulong)anonDataAttr.Record.DataLength;
                fileName.AllocatedSize = (ulong)anonDataAttr.Record.AllocatedLength;
            }

            if (updateMftRecord)
            {
                foreach (StructuredNtfsAttribute<FileNameRecord> attr in GetAttributes(AttributeType.FileName))
                {
                    if (attr.Content.ParentDirectory == fileName.ParentDirectory
                        && attr.Content.FileNameNamespace == fileName.FileNameNamespace
                        && attr.Content.FileName == fileName.FileName)
                    {
                        SetAttributeContent<FileNameRecord>(attr.Id, fileName);
                    }
                }
            }
        }

        public DirectoryEntry DirectoryEntry
        {
            get
            {
                FileNameRecord record = GetAttributeContent<FileNameRecord>(AttributeType.FileName);

                // Root dir is stored without root directory flag set in FileNameRecord, simulate it.
                if (_baseRecord.MasterFileTableIndex == MasterFileTable.RootDirIndex)
                {
                    record.Flags |= FileAttributeFlags.Directory;
                }

                return new DirectoryEntry(_context.GetDirectoryByRef(record.ParentDirectory), MftReference, record);
            }
        }

        internal INtfsContext Context
        {
            get
            {
                return _context;
            }
        }

        internal long GetAttributeOffset(ushort id)
        {
            return _baseRecord.GetAttributeOffset(id);
        }


        public virtual void Dump(TextWriter writer, string indent)
        {
            writer.WriteLine(indent + "FILE (" + ToString() + ")");
            writer.WriteLine(indent + "  File Number: " + _baseRecord.MasterFileTableIndex);

            _baseRecord.Dump(writer, indent + "  ");

            foreach (AttributeRecord attrRec in _baseRecord.Attributes)
            {
                NtfsAttribute.FromRecord(this, attrRec).Dump(writer, indent + "  ");
            }
        }

        public string BestName
        {
            get
            {
                NtfsAttribute[] attrs = GetAttributes(AttributeType.FileName);

                string longName = null;
                int longest = 0;

                if (attrs != null && attrs.Length != 0)
                {
                    longName = attrs[0].ToString();

                    for (int i = 1; i < attrs.Length; ++i)
                    {
                        string name = attrs[i].ToString();

                        if (Utilities.Is8Dot3(longName))
                        {
                            longest = i;
                            longName = name;
                        }
                    }
                }

                return longName;
            }
        }

        public override string ToString()
        {
            string bestName = BestName;
            if (bestName == null)
            {
                return "?????";
            }
            else
            {
                return bestName;
            }
        }

        private NtfsAttribute InnerGetAttribute(AttributeRecord record)
        {
            NtfsAttribute result = _attributeCache[record.AttributeId];
            if (result == null)
            {
                result = NtfsAttribute.FromRecord(this, record);
                _attributeCache[record.AttributeId] = result;
            }
            return result;
        }

        private IEnumerable<AttributeRecord> AllAttributeRecords
        {
            get
            {
                AttributeRecord attrListRec = _baseRecord.GetAttribute(AttributeType.AttributeList);
                if (attrListRec != null)
                {
                    StructuredNtfsAttribute<AttributeList> attrList = new StructuredNtfsAttribute<AttributeList>(this, attrListRec);
                    foreach (var record in attrList.Content)
                    {
                        FileRecord fileRec = _mft.GetRecord(record.BaseFileReference);
                        yield return fileRec.GetAttribute(record.AttributeId);
                    }
                }
                else
                {
                    foreach (var record in _baseRecord.Attributes)
                    {
                        yield return record;
                    }
                }
            }
        }


        internal static File CreateNew(INtfsContext context)
        {
            DateTime now = DateTime.UtcNow;

            File newFile = context.AllocateFile(FileRecordFlags.None);

            ushort attrId = newFile.CreateAttribute(AttributeType.StandardInformation);
            StandardInformation si = new StandardInformation();
            si.CreationTime = now;
            si.ModificationTime = now;
            si.MftChangedTime = now;
            si.LastAccessTime = now;
            si.FileAttributes = FileAttributeFlags.Archive;
            newFile.SetAttributeContent(attrId, si);

            Guid newId = CreateNewGuid(context);
            attrId = newFile.CreateAttribute(AttributeType.ObjectId);
            ObjectId objId = new ObjectId();
            objId.Id = newId;
            newFile.SetAttributeContent(attrId, objId);
            context.ObjectIds.Add(newId, newFile.MftReference, newId, Guid.Empty, Guid.Empty);

            newFile.CreateAttribute(AttributeType.Data);

            newFile.UpdateRecordInMft();

            return newFile;
        }

        internal void Delete()
        {
            if (_baseRecord.HardLinkCount != 0)
            {
                throw new InvalidOperationException("Attempt to delete in-use file: " + ToString());
            }

            StructuredNtfsAttribute<ObjectId> objIdAttr = (StructuredNtfsAttribute<ObjectId>)GetAttribute(AttributeType.ObjectId);
            if (objIdAttr != null)
            {
                Context.ObjectIds.Remove(objIdAttr.Content.Id);
            }

            List<NtfsAttribute> attrs = new List<NtfsAttribute>(AllAttributes);
            foreach (var attr in attrs)
            {
                RemoveAttribute(attr.Id);
            }

            _context.DeleteFile(this);
        }

        private static Guid CreateNewGuid(INtfsContext context)
        {
            Random rng = context.Options.RandomNumberGenerator;
            if (rng != null)
            {
                byte[] buffer = new byte[16];
                rng.NextBytes(buffer);
                return new Guid(buffer);
            }
            else
            {
                return Guid.NewGuid();
            }
        }
    }
}

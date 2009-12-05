﻿using System;
using System.IO;
using System.Diagnostics;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using MSDefragLib.IO;

namespace MSDefragLib.FileSystem.Ntfs
{
    public class MSScanNtfsEventArgs : EventArgs
    {
        public UInt32 m_level;
        public String m_message;

        public MSScanNtfsEventArgs(UInt32 level, String message)
        {
            m_level = level;
            m_message = message;
        }
    }

    class Scan
    {
        const UInt64 MFTBUFFERSIZE = 256 * 1024;

        private MSDefragLib m_msDefragLib;

        public Scan(MSDefragLib lib)
        {
            m_msDefragLib = lib;
        }

        public delegate void ShowDebugHandler(object sender, EventArgs e);

        public event ShowDebugHandler ShowDebugEvent;

        protected virtual void OnShowDebug(EventArgs e)
        {
            if (ShowDebugEvent != null)
            {
                ShowDebugEvent(this, e);
            }
        }

        public void ShowDebug(UInt32 level, String output)
        {
            MSScanNtfsEventArgs e = new MSScanNtfsEventArgs(level, output);

            if (level < 6)
            {
                Console.Out.WriteLine(output);
                OnShowDebug(e);
            }
        }

        /// <summary>
        /// If expression is true, exception is thrown
        /// </summary>
        /// <param name="expression"></param>
        /// <param name="message"></param>
        public Boolean ErrorCheck(Boolean expression, String message, Boolean throwException)
        {
            if (expression && throwException)
            {
                throw new Exception(message);
            }

            return expression;
        }

        /// <summary>
        /// Fixup the raw MFT data that was read from disk. Return true if everything is ok,
        /// false if the MFT data is corrupt (this can also happen when we have read a
        /// record past the end of the MFT, maybe it has shrunk while we were processing).
        /// 
        /// - To protect against disk failure, the last 2 bytes of every sector in the MFT are
        ///   not stored in the sector itself, but in the "Usa" array in the header (described
        ///   by UsaOffset and UsaCount). The last 2 bytes are copied into the array and the
        ///   Update Sequence Number is written in their place.
        ///
        /// - The Update Sequence Number is stored in the first item (item zero) of the "Usa"
        ///   array.
        ///
        /// - The number of bytes per sector is defined in the $Boot record.
        /// 
        /// Throws InvalidDataException on parse problems.
        /// </summary>
        /// <param name="DiskInfo"></param>
        /// <param name="Buffer"></param>
        /// <param name="BufLength"></param>
        private void FixupRawMftdata(DiskInformation DiskInfo, ByteArray buffer, UInt64 BufLength)
        {
            UInt32 record = BitConverter.ToUInt32(buffer.m_bytes, 0);

            /* If this is not a FILE record then return FALSE. */
            if (record != 0x454c4946)
            {
                ShowDebug(2, "This is not a valid MFT record, it does not begin with FILE (maybe trying to read past the end?).");
                //m_msDefragLib.ShowHex(Data, Buffer.m_bytes, BufLength);
                throw new InvalidDataException();
            }

            // Walk through all the sectors and restore the last 2 bytes with the value
            // from the Usa array. If we encounter bad sector data then return with false. 
            UInt16Array BufferW = buffer.ToUInt16Array(0, buffer.GetLength());

            RecordHeader RecordHeader = RecordHeader.Parse(Helper.BinaryReader(buffer));
            UInt16Array UpdateSequenceArray = buffer.ToUInt16Array(RecordHeader.UsaOffset, buffer.GetLength() - RecordHeader.UsaOffset);
            Int64 Increment = (Int64)(DiskInfo.BytesPerSector / sizeof(UInt16));

            Int64 index = Increment - 1;

            for (UInt16 i = 1; i < RecordHeader.UsaCount; i++)
            {
                // Check if we are inside the buffer.
                if (index * sizeof(UInt16) >= (Int64)BufLength)
                {
                    ShowDebug(0, "Warning: USA data indicates that data is missing, the MFT may be corrupt.");
                    throw new InvalidDataException();
                }

                // Check if the last 2 bytes of the sector contain the Update Sequence Number.
                // If not then return FALSE.
                if (BufferW.GetValue(index) != UpdateSequenceArray.GetValue(0))
                {
                    ShowDebug(0, "Error: USA fixup word is not equal to the Update Sequence Number, the MFT may be corrupt.");
                    throw new InvalidDataException();
                }

                // Replace the last 2 bytes in the sector with the value from the Usa array.
                BufferW.SetValue(index, UpdateSequenceArray.GetValue(i));

                index += Increment;
            }

            buffer = BufferW.ToByteArray(0, BufferW.GetLength());
        }

        /// <summary>
        /// Read the data that is specified in a RunData list from disk into memory,
        /// skipping the first Offset bytes. Return a malloc'ed buffer with the data,
        /// or null if error.
        /// </summary>
        /// <param name="DiskInfo"></param>
        /// <param name="RunData"></param>
        /// <param name="RunDataLength"></param>
        /// <param name="Offset">Bytes to skip from begin of data.</param>
        /// <param name="WantedLength">Number of bytes to read.</param>
        /// <returns></returns>
        ByteArray ReadNonResidentData(
                    DiskInformation DiskInfo,
                    BinaryReader runData,
                    UInt64 runDataLength,
                    UInt64 Offset,
                    UInt64 WantedLength)
        {
            ByteArray Buffer = new ByteArray((Int64)WantedLength);

            UInt64 ExtentVcn;
            UInt64 ExtentLcn;
            UInt64 ExtentLength;

            ShowDebug(6, String.Format("    Reading {0:G} bytes from offset {0:G}", WantedLength, Offset));

            ErrorCheck((runData == null) || (runDataLength == 0), "Sanity check failed!", true);

            if (WantedLength >= UInt32.MaxValue)
            {
                ShowDebug(2, String.Format("    Cannot read {0:G} bytes, maximum is {1:G}.", WantedLength, UInt32.MaxValue));

                return null;
            }

            // We have to round up the WantedLength to the nearest sector. For some
            // reason or other Microsoft has decided that raw reading from disk can
            // only be done by whole sector, even though ReadFile() accepts it's
            // parameters in bytes.
            //
            if (WantedLength % DiskInfo.BytesPerSector > 0)
            {
                WantedLength = WantedLength + DiskInfo.BytesPerSector - WantedLength % DiskInfo.BytesPerSector;
            }

            // Walk through the RunData and read the requested data from disk.
            Int64 Lcn = 0;
            UInt64 Vcn = 0;

            UInt64 runLength;
            Int64 runOffset;
            while (RunData.Parse(runData, out runLength, out runOffset))
            {
                Lcn += runOffset;

                // Ignore virtual extents.
                if (runOffset == 0)
                    continue;

                // I don't think the RunLength can ever be zero, but just in case.
                if (runLength == 0)
                    continue;

                // Determine how many and which bytes we want to read. If we don't need
                // any bytes from this extent then loop.
                //
                ExtentVcn = Vcn * DiskInfo.BytesPerCluster;
                ExtentLcn = (UInt64)((UInt64)Lcn * DiskInfo.BytesPerCluster);

                ExtentLength = runLength * DiskInfo.BytesPerCluster;

                if (Offset >= ExtentVcn + ExtentLength) continue;

                if (Offset > ExtentVcn)
                {
                    ExtentLcn = ExtentLcn + Offset - ExtentVcn;
                    ExtentLength = ExtentLength - (Offset - ExtentVcn);
                    ExtentVcn = Offset;
                }

                if (Offset + WantedLength <= ExtentVcn) continue;

                if (Offset + WantedLength < ExtentVcn + ExtentLength)
                {
                    ExtentLength = Offset + WantedLength - ExtentVcn;
                }

                if (ExtentLength == 0) continue;

                // Read the data from the disk. If error then return FALSE.
                //
                ShowDebug(6, String.Format("    Reading {0:G} bytes from Lcn={1:G} into offset={2:G}",
                    ExtentLength, ExtentLcn / DiskInfo.BytesPerCluster,
                    ExtentVcn - Offset));

                m_msDefragLib.Data.Disk.ReadFromCluster(ExtentLcn, Buffer.m_bytes,
                    (Int32)(ExtentVcn - Offset), (Int32)ExtentLength);

                Vcn += runLength;
            }

            return (Buffer);
        }

        /// <summary>
        /// Read the RunData list and translate into a list of fragments. 
        /// </summary>
        /// <param name="InodeData"></param>
        /// <param name="StreamName"></param>
        /// <param name="StreamType"></param>
        /// <param name="runData"></param>
        /// <param name="runDataLength"></param>
        /// <param name="startingVcn"></param>
        /// <param name="Bytes"></param>
        /// <returns></returns>
        Boolean TranslateRundataToFragmentlist(
                    InodeDataStructure InodeData,
                    String StreamName,
                    AttributeType StreamType,
                    BinaryReader runData,
                    UInt64 runDataLength,
                    UInt64 startingVcn,
                    UInt64 byteCount)
        {
            ErrorCheck((m_msDefragLib.Data == null) || (InodeData == null), "Sanity check failed", true);

            // Find the stream in the list of streams. If not found then create a new stream.
            Stream foundStream = InodeData.Streams.FirstOrDefault(x => (x.Name == StreamName) && (x.Type.Type == StreamType.Type));
            if (foundStream == null)
            {
                ShowDebug(6, "    Creating new stream: '" + StreamName + ":" + StreamType.GetStreamTypeName() + "'");
                Stream newStream = new Stream(StreamName, StreamType);
                newStream.Bytes = byteCount;

                InodeData.Streams.Add(newStream);
                foundStream = newStream;
            }
            else
            {
                ShowDebug(6, "    Appending rundata to existing stream: '" + StreamName + ":" + StreamType.GetStreamTypeName());
                if (foundStream.Bytes == 0)
                    foundStream.Bytes = byteCount;
            }

            if (runData == null)
                return true;

            foundStream.ParseRunData(runData, startingVcn);
            return true;
        }

        /* Construct the full stream name from the filename, the stream name, and the stream type. */
        private String ConstructStreamName(String fileName1, String fileName2, Stream thisStream)
        {
            String fileName = fileName1 ?? fileName2;

            String streamName = null;
            AttributeType type = new AttributeType();

            if (thisStream != null)
            {
                streamName = thisStream.Name;
                type = thisStream.Type;
            }

            // If the StreamName is empty and the StreamType is Data then return only the
            // FileName. The Data stream is the default stream of regular files.
            //
            if ((String.IsNullOrEmpty(streamName)) && type.IsData)
            {
                return fileName;
            }

            // If the StreamName is "$I30" and the StreamType is AttributeIndexAllocation then
            // return only the FileName. This must be a directory, and the Microsoft 
            // defragmentation API will automatically select this stream.
            //
            if ((streamName == "$I30") && type.IsIndexAllocation)
            {
                return fileName;
            }

            //  If the StreamName is empty and the StreamType is Data then return only the
            //  FileName. The Data stream is the default stream of regular files.
            if (String.IsNullOrEmpty(streamName) &&
                String.IsNullOrEmpty(type.GetStreamTypeName()))
            {
                return fileName;
            }

            Int32 Length = 3;

            if (fileName != null) 
                Length += fileName.Length;
            if (streamName != null)
                Length += streamName.Length;

            Length += type.GetStreamTypeName().Length;

            if (Length == 3) return (null);

            StringBuilder p1 = new StringBuilder();
            if (!String.IsNullOrEmpty(fileName))
                p1.Append(fileName);
            p1.Append(":");

            if (!String.IsNullOrEmpty(streamName))
                p1.Append(streamName);
            p1.Append(":");
            p1.Append(type.GetStreamTypeName());

            return p1.ToString();
        }

        /// <summary>
        /// Process a list of attributes and store the gathered information in the Item
        /// struct. Return FALSE if an error occurred.
        /// </summary>
        /// <param name="DiskInfo"></param>
        /// <param name="inodeData"></param>
        /// <param name="reader"></param>
        /// <param name="BufLength"></param>
        /// <param name="Depth"></param>
        void ProcessAttributeList(
                DiskInformation diskInfo,
                InodeDataStructure inodeData,
                BinaryReader reader,
                UInt64 BufLength,
                int Depth)
        {
            Debug.Assert(inodeData.MftDataFragments != null);

            Int64 position = reader.BaseStream.Position;
            ByteArray Buffer2 = new ByteArray((Int64)diskInfo.BytesPerMftRecord);

            FileRecordHeader FileRecordHeader;

            UInt64 BaseInode;
            UInt64 RealVcn;

            String p1;

            ErrorCheck((reader == null) || (BufLength == 0), "Sanity check failed", true);
            ErrorCheck((Depth > 1000), "Error: infinite attribute loop", false);

            ShowDebug(6, String.Format("Processing AttributeList for m_iNode {0:G}, {1:G} bytes", inodeData.m_iNode, BufLength));

            AttributeList attributeList = null;

            // Walk through all the attributes and gather information.
            //
            for (Int64 AttributeOffset = 0; AttributeOffset < (Int64)BufLength; AttributeOffset += attributeList.Length)
            {
                reader.BaseStream.Seek(position + AttributeOffset, SeekOrigin.Begin);
                attributeList = AttributeList.Parse(reader);

                // Exit if no more attributes. AttributeLists are usually not closed by the
                // 0xFFFFFFFF endmarker. Reaching the end of the buffer is therefore normal and
                // not an error.
                //
                if (AttributeOffset + 3 > (Int64)BufLength) break;
                if (attributeList.Type.IsEndOfList) break;
                if (attributeList.Length < 3) break;
                if (AttributeOffset + attributeList.Length > (Int64)BufLength) break;

                // Extract the referenced m_iNode. If it's the same as the calling m_iNode then 
                // ignore (if we don't ignore then the program will loop forever, because for 
                // some reason the info in the calling m_iNode is duplicated here...).
                //
                UInt64 RefInode = attributeList.m_fileReferenceNumber.BaseInodeNumber;
                    //(UInt64)attributeList.m_fileReferenceNumber.m_iNodeNumberLowPart +
                    //    ((UInt64)attributeList.m_fileReferenceNumber.m_iNodeNumberHighPart << 32);

                if (RefInode == inodeData.m_iNode) continue;

                // Show debug message.
                //ShowDebug(6, "    List attribute: " + attributeList.Type.GetStreamTypeName());
                //ShowDebug(6, String.Format("      m_lowestVcn = {0:G}, RefInode = {1:G}, InodeSequence = {2:G}, m_instance = {3:G}",
                //      attributeList.m_lowestVcn, RefInode, attributeList.m_fileReferenceNumber.m_sequenceNumber, attributeList.m_instance));

                // Extract the streamname. I don't know why AttributeLists can have names, and
                // the name is not used further down. It is only extracted for debugging 
                // purposes.
                //
                if (attributeList.NameLength > 0)
                {
                    reader.BaseStream.Seek(position + AttributeOffset + attributeList.NameOffset, SeekOrigin.Begin);
                    p1 = Helper.ParseString(reader, attributeList.NameLength);
                    ShowDebug(6, "      AttributeList name = '" + p1 + "'");
                }

                // Find the fragment in the MFT that contains the referenced m_iNode.
                Fragment foundFragment = inodeData.MftDataFragments.FindContaining(
                    diskInfo.InodeToCluster(RefInode));

                // Fetch the record of the referenced m_iNode from disk.
                UInt64 tempVcn = diskInfo.ClusterToBytes(foundFragment.Lcn) + diskInfo.InodeToBytes(RefInode);

                m_msDefragLib.Data.Disk.ReadFromCluster(tempVcn, Buffer2.m_bytes, 0,
                    (Int32)diskInfo.BytesPerMftRecord);

                FixupRawMftdata(diskInfo, Buffer2, diskInfo.BytesPerMftRecord);

                // If the Inode is not in use then skip.
                FileRecordHeader = FileRecordHeader.Parse(Helper.BinaryReader(Buffer2));

                if (!FileRecordHeader.IsInUse)
                {
                    ShowDebug(6, String.Format("      Referenced m_iNode {0:G} is not in use.", RefInode));
                    continue;
                }

                // If the BaseInode inside the m_iNode is not the same as the calling m_iNode then skip.
                BaseInode = FileRecordHeader.BaseFileRecord.BaseInodeNumber;
                //BaseInode = (UInt64)FileRecordHeader.BaseFileRecord.m_iNodeNumberLowPart +
                //        ((UInt64)FileRecordHeader.BaseFileRecord.m_iNodeNumberHighPart << 32);

                if (inodeData.m_iNode != BaseInode)
                {
                    ShowDebug(6, String.Format("      Warning: m_iNode {0:G} is an extension of m_iNode {1:G}, but thinks it's an extension of m_iNode {2:G}.",
                            RefInode, inodeData.m_iNode, BaseInode));

                    continue;
                }

                // Process the list of attributes in the m_iNode, by recursively calling the ProcessAttributes() subroutine.
                ShowDebug(6, String.Format("      Processing m_iNode {0:G} m_instance {1:G}", RefInode, attributeList.m_instance));

                ProcessAttributes(diskInfo, inodeData,
                    Helper.BinaryReader(Buffer2, FileRecordHeader.AttributeOffset),
                    diskInfo.BytesPerMftRecord - FileRecordHeader.AttributeOffset,
                    attributeList.m_instance, Depth + 1);

                ShowDebug(6, String.Format("      Finished processing m_iNode {0:G} m_instance {1:G}", RefInode, attributeList.m_instance));
            }
        }

        /// <summary>
        /// Process a list of attributes and store the gathered information in the Item
        /// struct. Return FALSE if an error occurred.
        /// </summary>
        /// <param name="DiskInfo"></param>
        /// <param name="InodeData"></param>
        /// <param name="Buffer"></param>
        /// <param name="BufLength"></param>
        /// <param name="Instance"></param>
        /// <param name="Depth"></param>
        /// <returns></returns>
        Boolean ProcessAttributes(
            DiskInformation DiskInfo,
            InodeDataStructure inodeData,
            BinaryReader reader, UInt64 BufLength,
            UInt32 Instance, int Depth)
        {
            UInt64 Buffer2Length;
            UInt32 AttributeOffset;

            Attribute attribute;
            ResidentAttribute residentAttribute;
            NonResidentAttribute nonResidentAttribute;
            StandardInformation standardInformation;
            FileNameAttribute fileNameAttribute;

            String p1;
            Int64 position = reader.BaseStream.Position;

            // Walk through all the attributes and gather information. AttributeLists are
            // skipped and interpreted later.
            //
            for (AttributeOffset = 0; AttributeOffset < BufLength; AttributeOffset += attribute.Length)
            {
                reader.BaseStream.Seek(position + AttributeOffset, SeekOrigin.Begin);
                attribute = Attribute.Parse(reader);

                if (attribute.Type.IsEndOfList)
                {
                    break;
                }

                // Exit the loop if end-marker.
                if ((AttributeOffset + 4 <= BufLength) && attribute.Type.IsInvalid)
                {
                    break;
                }

                ErrorCheck(
                    (AttributeOffset + 4 > BufLength) ||
                    (attribute.Length < 3) ||
                    (AttributeOffset + attribute.Length > BufLength),
                    String.Format("Error: attribute in m_iNode {0:G} is bigger than the data, the MFT may be corrupt.", inodeData.m_iNode), true);

                // Skip AttributeList's for now.
                if (attribute.Type.IsAttributeList)
                {
                    continue;
                }

                // If the Instance does not equal the m_attributeNumber then ignore the attribute.
                // This is used when an AttributeList is being processed and we only want a specific
                // instance.
                //
                if ((Instance != UInt16.MaxValue) && (Instance != attribute.Number))
                {
                    continue;
                }

                // Show debug message.
                ShowDebug(6, String.Format("  Attribute {0:G}: {1:G}", attribute.Number, attribute.Type.GetStreamTypeName()));

                reader.BaseStream.Seek(position + AttributeOffset, SeekOrigin.Begin);
                if (attribute.IsNonResident == false)
                {
                    residentAttribute = ResidentAttribute.Parse(reader);
                    Int64 tempOffset = (Int64)(AttributeOffset + residentAttribute.ValueOffset);
                    reader.BaseStream.Seek(position + tempOffset, SeekOrigin.Begin);

                    // The AttributeFileName (0x30) contains the filename and the link to the parent directory.
                    if (attribute.Type.IsFileName)
                    {
                        fileNameAttribute = FileNameAttribute.Parse(reader);

                        inodeData.m_parentInode = fileNameAttribute.m_parentDirectory.BaseInodeNumber;

                        //inodeData.m_parentInode = fileNameAttribute.m_parentDirectory.m_iNodeNumberLowPart +
                        //    (((UInt32)fileNameAttribute.m_parentDirectory.m_iNodeNumberHighPart) << 32);
                        inodeData.AddName(fileNameAttribute);
                    }

                    //  The AttributeStandardInformation (0x10) contains the m_creationTime,
                    //  m_lastAccessTime, the m_mftChangeTime, and the file attributes.
                    if (attribute.Type.IsStandardInformation)
                    {
                        standardInformation = StandardInformation.Parse(reader);

                        inodeData.m_creationTime = standardInformation.CreationTime;
                        inodeData.m_mftChangeTime = standardInformation.MftChangeTime;
                        inodeData.m_lastAccessTime = standardInformation.LastAccessTime;
                    }

                    // The value of the AttributeData (0x80) is the actual data of the file.
                    if (attribute.Type.IsData)
                    {
                        inodeData.m_totalBytes = residentAttribute.ValueLength;
                    }
                }
                else
                {
                    nonResidentAttribute = NonResidentAttribute.Parse(reader);

                    // Save the length (number of bytes) of the data.
                    if (attribute.Type.IsData && (inodeData.m_totalBytes == 0))
                    {
                        inodeData.m_totalBytes = nonResidentAttribute.m_dataSize;
                    }

                    // Extract the streamname.
                    reader.BaseStream.Seek(position + AttributeOffset + attribute.NameOffset, SeekOrigin.Begin);

                    p1 = Helper.ParseString(reader, attribute.NameLength);

                    // Create a new stream with a list of fragments for this data.
                    reader.BaseStream.Seek(position + AttributeOffset + nonResidentAttribute.m_runArrayOffset, SeekOrigin.Begin);

                    TranslateRundataToFragmentlist(inodeData, p1, attribute.Type,
                        reader, attribute.Length - nonResidentAttribute.m_runArrayOffset,
                        nonResidentAttribute.m_startingVcn, nonResidentAttribute.m_dataSize);

                    // Special case: If this is the $MFT then save data.
                    if (inodeData.m_iNode == 0)
                    {
                        if (attribute.Type.IsData && (inodeData.MftDataFragments == null))
                        {
                            inodeData.MftDataFragments = inodeData.Streams.First().Fragments;
                            inodeData.MftDataLength = nonResidentAttribute.m_dataSize;
                        }

                        if (attribute.Type.IsBitmap && (inodeData.MftBitmapFragments == null))
                        {
                            inodeData.MftBitmapFragments = inodeData.Streams.First().Fragments;
                            inodeData.MftBitmapLength = nonResidentAttribute.m_dataSize;
                        }
                    }
                }
            }

            // Walk through all the attributes and interpret the AttributeLists. We have to
            // do this after the DATA and BITMAP attributes have been interpreted, because
            // some MFT's have an AttributeList that is stored in fragments that are
            // defined in the DATA attribute, and/or contain a continuation of the DATA or
            // BITMAP attributes.
            //
            for (AttributeOffset = 0; AttributeOffset < BufLength; AttributeOffset += attribute.Length)
            {
                reader.BaseStream.Seek(position + AttributeOffset, SeekOrigin.Begin);
                //HACK: temporary hack to demonstrate the usage of the binary reader
                attribute = Attribute.Parse(reader);

                if (attribute.Type.IsEndOfList || attribute.Type.IsInvalid)
                {
                    break;
                }

                if (!attribute.Type.IsAttributeList)
                {
                    continue;
                }

                ShowDebug(6, String.Format("  Attribute {0:G}: {1:G}", attribute.Number, attribute.Type.GetStreamTypeName()));

                reader.BaseStream.Seek(position + AttributeOffset, SeekOrigin.Begin);
                if (attribute.IsNonResident == false)
                {
                    residentAttribute = ResidentAttribute.Parse(reader);

                    reader.BaseStream.Seek(position + AttributeOffset + residentAttribute.ValueOffset, SeekOrigin.Begin);
                    ProcessAttributeList(DiskInfo, inodeData, reader, residentAttribute.ValueLength, Depth);
                }
                else
                {
                    nonResidentAttribute = NonResidentAttribute.Parse(reader);

                    Buffer2Length = nonResidentAttribute.m_dataSize;

                    reader.BaseStream.Seek(position + AttributeOffset + nonResidentAttribute.m_runArrayOffset, SeekOrigin.Begin);
                    ByteArray Buffer2 = ReadNonResidentData(DiskInfo, reader,
                        attribute.Length - nonResidentAttribute.m_runArrayOffset, 0, Buffer2Length);

                    ProcessAttributeList(DiskInfo, inodeData, Helper.BinaryReader(Buffer2), Buffer2Length, Depth);
                }
            }

            return true;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="DiskInfo"></param>
        /// <param name="InodeArray"></param>
        /// <param name="InodeNumber"></param>
        /// <param name="MaxInode"></param>
        /// <param name="mftDataFragments"></param>
        /// <param name="mftDataBytes"></param>
        /// <param name="mftBitmapFragments"></param>
        /// <param name="mftBitmapBytes"></param>
        /// <param name="reader"></param>
        /// <param name="BufLength"></param>
        /// <returns></returns>
        Boolean InterpretMftRecord(
            DiskInformation DiskInfo, Array InodeArray,
            UInt64 InodeNumber, UInt64 MaxInode,
            ref FragmentList mftDataFragments, ref UInt64 mftDataBytes,
            ref FragmentList mftBitmapFragments, ref UInt64 mftBitmapBytes,
            BinaryReader reader, UInt64 BufLength)
        {
            Int64 position = reader.BaseStream.Position;
            FileRecordHeader fileRecordHeader = FileRecordHeader.Parse(reader);

            // If the record is not in use then quietly exit
            if (!fileRecordHeader.IsInUse)
            {
                ShowDebug(6, String.Format("Inode {0:G} is not in use.", InodeNumber));
                return false;
            }

            // If the record has a BaseFileRecord then ignore it. It is used by an
            // AttributeAttributeList as an extension of another m_iNode, it's not an
            // Inode by itself.
            //
            UInt64 BaseInode = fileRecordHeader.BaseFileRecord.BaseInodeNumber;

            if (BaseInode != 0)
            {
                ShowDebug(6, String.Format("Ignoring Inode {0:G}, it's an extension of Inode {1:G}", InodeNumber, BaseInode));
                return true;
            }

            // ShowDebug(6, String.Format("Processing Inode {0:G}...", InodeNumber));

            // Show a warning if the Flags have an unknown value.
            if (fileRecordHeader.IsUnknown)
            {
                // ShowDebug(6, String.Format("  Inode {0:G} has Flags = {1:G}", InodeNumber, fileRecordHeader.Flags));
            }

            // I think the MFTRecordNumber should always be the InodeNumber, but it's an XP
            // extension and I'm not sure about Win2K.
            // 
            // Note: why is the MFTRecordNumber only 32 bit? Inode numbers are 48 bit.
            //
            ErrorCheck(fileRecordHeader.MFTRecordNumber != InodeNumber,
                String.Format("Warning: m_iNode {0:G} contains a different MFTRecordNumber {1:G}",
                      InodeNumber, fileRecordHeader.MFTRecordNumber), true);

            ErrorCheck(
                fileRecordHeader.AttributeOffset >= BufLength,
                String.Format("Error: attributes in m_iNode {0:G} are outside the FILE record, the MFT may be corrupt.",
                      InodeNumber), 
                 true);

            ErrorCheck(
                fileRecordHeader.BytesInUse > BufLength,
                String.Format("Error: in m_iNode {0:G} the record is bigger than the size of the buffer, the MFT may be corrupt.",
                      InodeNumber), 
                true);

            InodeDataStructure inodeData = new InodeDataStructure(InodeNumber);

            inodeData.IsDirectory = fileRecordHeader.IsDirectory;
            inodeData.MftDataFragments = mftDataFragments;
            inodeData.MftDataLength = mftDataBytes;

            // Make sure that directories are always created.
            if (inodeData.IsDirectory)
            {
                AttributeType attributeType = AttributeTypeEnum.AttributeIndexAllocation;
                TranslateRundataToFragmentlist(inodeData, "$I30", attributeType, null, 0, 0, 0);
            }

            // Interpret the attributes.
            reader.BaseStream.Seek(position + fileRecordHeader.AttributeOffset, SeekOrigin.Begin);

            ProcessAttributes(DiskInfo, inodeData,
                reader, BufLength - fileRecordHeader.AttributeOffset, UInt16.MaxValue, 0);

            // Save the MftDataFragments, MftDataBytes, MftBitmapFragments, and MftBitmapBytes.
            if (InodeNumber == 0)
            {
                mftDataFragments = inodeData.MftDataFragments;
                mftDataBytes = inodeData.MftDataLength;
                mftBitmapFragments = inodeData.MftBitmapFragments;
                mftBitmapBytes = inodeData.MftBitmapLength;
            }

            // Create an item in the Data->ItemTree for every stream.
            foreach (Stream stream in inodeData.Streams)
            {
                // Create and fill a new item record in memory.
                ItemStruct Item = new ItemStruct(stream);
                Item.LongFilename = ConstructStreamName(inodeData.LongFilename, inodeData.ShortFilename, stream);
                Item.LongPath = null;

                Item.ShortFilename = ConstructStreamName(inodeData.ShortFilename, inodeData.LongFilename, stream);
                Item.ShortPath = null;

                Item.Bytes = inodeData.m_totalBytes;

                Item.Bytes = stream.Bytes;

                Item.Clusters = 0;

                Item.Clusters = stream.Clusters;

                Item.CreationTime = inodeData.m_creationTime;
                Item.MftChangeTime = inodeData.m_mftChangeTime;
                Item.LastAccessTime = inodeData.m_lastAccessTime;

                Item.ParentInode = inodeData.m_parentInode;
                Item.Directory = inodeData.IsDirectory;
                Item.Unmovable = false;
                Item.Exclude = false;
                Item.SpaceHog = false;

                // Increment counters
                if (Item.Directory == true)
                {
                    m_msDefragLib.Data.CountDirectories++;
                }

                m_msDefragLib.Data.CountAllFiles++;

                if (stream.Type.IsData)
                {
                    m_msDefragLib.Data.CountAllBytes += inodeData.m_totalBytes;
                }

                m_msDefragLib.Data.CountAllClusters += stream.Clusters;

                if (Item.FragmentCount > 1)
                {
                    m_msDefragLib.Data.CountFragmentedItems++;
                    m_msDefragLib.Data.CountFragmentedBytes += inodeData.m_totalBytes;

                    if (stream != null) m_msDefragLib.Data.CountFragmentedClusters += stream.Clusters;
                }

                // Add the item record to the sorted item tree in memory.
                m_msDefragLib.TreeInsert(Item);

                //  Also add the item to the array that is used to construct the full pathnames.
                //
                //  NOTE:
                //  If the array already contains an entry, and the new item has a shorter
                //  filename, then the entry is replaced. This is needed to make sure that
                //  the shortest form of the name of directories is used.
                //
                ItemStruct InodeItem = null;

                if (InodeArray != null && InodeNumber < MaxInode)
                {
                    InodeItem = (ItemStruct)InodeArray.GetValue((Int64)InodeNumber);
                }

                String InodeLongFilename = "";

                if (InodeItem != null)
                {
                    InodeLongFilename = InodeItem.LongFilename;
                }

                if (InodeLongFilename.CompareTo(Item.LongFilename) > 0)
                {
                    InodeArray.SetValue(Item, (Int64)InodeNumber);
                }

                // Draw the item on the screen.
                //jkGui->ShowAnalyze(Data,Item);

                if (m_msDefragLib.Data.RedrawScreen == 0)
                {
                    m_msDefragLib.ColorizeItem(Item, 0, 0, false);
                }
                else
                {
                    m_msDefragLib.ShowDiskmap();
                }
            }

            return true;
        }

        /// <summary>
        /// Load the MFT into a list of ItemStruct records in memory
        /// </summary>
        /// <returns></returns>
        public Boolean AnalyzeNtfsVolume()
        {
            // Read the boot block from the disk.
            FS.IBootSector bootSector = m_msDefragLib.Data.Disk.BootSector;

            // Test if the boot block is an NTFS boot block.
            if (bootSector.Filesystem != FS.Filesystem.NTFS)
            {
                ShowDebug(2, "This is not an NTFS disk (different cookie).");
                return false;
            }

            DiskInformation diskInfo = new DiskInformation(bootSector);

            m_msDefragLib.Data.BytesPerCluster = diskInfo.BytesPerCluster;

            if (diskInfo.SectorsPerCluster > 0)
            {
                m_msDefragLib.Data.TotalClusters = diskInfo.TotalSectors / diskInfo.SectorsPerCluster;
            }

            ShowDebug(0, "This is an NTFS disk.");

            ShowDebug(2, String.Format("  Disk cookie: {0:X}", bootSector.OemId));
            ShowDebug(2, String.Format("  BytesPerSector: {0:G}", diskInfo.BytesPerSector));
            ShowDebug(2, String.Format("  TotalSectors: {0:G}", diskInfo.TotalSectors));
            ShowDebug(2, String.Format("  SectorsPerCluster: {0:G}", diskInfo.SectorsPerCluster));

            ShowDebug(2, String.Format("  SectorsPerTrack: {0:G}", bootSector.SectorsPerTrack));
            ShowDebug(2, String.Format("  NumberOfHeads: {0:G}", bootSector.NumberOfHeads));
            ShowDebug(2, String.Format("  MftStartLcn: {0:G}", diskInfo.MftStartLcn));
            ShowDebug(2, String.Format("  Mft2StartLcn: {0:G}", diskInfo.Mft2StartLcn));
            ShowDebug(2, String.Format("  BytesPerMftRecord: {0:G}", diskInfo.BytesPerMftRecord));
            ShowDebug(2, String.Format("  ClustersPerIndexRecord: {0:G}", diskInfo.ClustersPerIndexRecord));

            ShowDebug(2, String.Format("  MediaType: {0:X}", bootSector.MediaType));

            ShowDebug(2, String.Format("  VolumeSerialNumber: {0:X}", bootSector.Serial));

            // Calculate the size of first 16 Inodes in the MFT. The Microsoft defragmentation
            // API cannot move these inodes.
            //
            m_msDefragLib.Data.Disk.MftLockedClusters = diskInfo.BytesPerCluster / diskInfo.BytesPerMftRecord;

            // Read the $MFT record from disk into memory, which is always the first record in
            // the MFT.
            //
            UInt64 tempLcn = diskInfo.MftStartLcn * diskInfo.BytesPerCluster;

            ByteArray Buffer = new ByteArray((Int64)MFTBUFFERSIZE);

            m_msDefragLib.Data.Disk.ReadFromCluster(tempLcn, Buffer.m_bytes, 0,
                (Int32)diskInfo.BytesPerMftRecord);

            FixupRawMftdata(diskInfo, Buffer, diskInfo.BytesPerMftRecord);

            // Extract data from the MFT record and put into an Item struct in memory. If
            // there was an error then exit.
            //
            FragmentList MftDataFragments = null;
            FragmentList MftBitmapFragments = null;

            UInt64 MftDataBytes = 0;
            UInt64 MftBitmapBytes = 0;

            Boolean Result = InterpretMftRecord(diskInfo, null, 0, 0,
                ref MftDataFragments, ref MftDataBytes, ref MftBitmapFragments, ref MftBitmapBytes,
                Helper.BinaryReader(Buffer), diskInfo.BytesPerMftRecord);

            ShowDebug(6, String.Format("MftDataBytes = {0:G}, MftBitmapBytes = {0:G}", MftDataBytes, MftBitmapBytes));

            BitmapFile bitmapFile = new BitmapFile(m_msDefragLib.Data.Disk,
                diskInfo, MftBitmapFragments, MftBitmapBytes, MftDataBytes);

            UInt64 MaxInode = bitmapFile.MaxInode;

            ItemStruct[] InodeArray = new ItemStruct[MaxInode];
            InodeArray[0] = m_msDefragLib.Data.ItemTree;
            ItemStruct Item = null;

            m_msDefragLib.Data.PhaseDone = 0;
            m_msDefragLib.Data.PhaseTodo = 0;

            DateTime startTime = DateTime.Now;

            m_msDefragLib.Data.PhaseTodo = bitmapFile.UsedInodes;

            // Read and process all the records in the MFT. The records are read into a
            // buffer and then given one by one to the InterpretMftRecord() subroutine.
            UInt64 BlockStart = 0;
            UInt64 BlockEnd = 0;
            UInt64 InodeNumber = 0;
            foreach (bool bit in bitmapFile.Bits)
            {
                // Ignore the m_iNode if the bitmap says it's not in use.
                if (!bit || (InodeNumber == 0))
                {
                    InodeNumber++;
                    continue;
                }

                // Update the progress counter
                m_msDefragLib.Data.PhaseDone++;

                // Read a block of inode's into memory
                if (InodeNumber >= BlockEnd)
                {
                    // Slow the program down to the percentage that was specified on the command line
                    m_msDefragLib.SlowDown();

                    BlockStart = InodeNumber;
                    BlockEnd = BlockStart + diskInfo.BytesToInode(MFTBUFFERSIZE);

                    if (BlockEnd > MftBitmapBytes * 8)
                        BlockEnd = MftBitmapBytes * 8;

                    Fragment foundFragment = MftDataFragments.FindContaining(
                        diskInfo.InodeToCluster(InodeNumber));

                    UInt64 u1 = diskInfo.ClusterToInode(foundFragment.NextVcn);
                    if (BlockEnd > u1)
                        BlockEnd = u1;

                    UInt64 lcn = diskInfo.ClusterToBytes(foundFragment.Lcn)+diskInfo.InodeToBytes(BlockStart);
                    
                    //Console.WriteLine("Reading block of {0} Inodes from MFT into memory, {1} bytes from LCN={2}",
                    //    BlockEnd - BlockStart, diskInfo.InodeToBytes(BlockEnd - BlockStart),
                    //    diskInfo.BytesToCluster(lcn));

                    m_msDefragLib.Data.Disk.ReadFromCluster(lcn,
                        Buffer.m_bytes, 0, (Int32)diskInfo.InodeToBytes(BlockEnd - BlockStart));
                }

                // Fixup the raw data of this m_iNode
                UInt64 position = diskInfo.InodeToBytes(InodeNumber - BlockStart);
                FixupRawMftdata(diskInfo,
                        Buffer.ToByteArray((Int64)position, Buffer.GetLength() - (Int64)(position)),
                        diskInfo.BytesPerMftRecord);

                // Interpret the m_iNode's attributes.
                Result = InterpretMftRecord(diskInfo, InodeArray, InodeNumber, MaxInode,
                        ref MftDataFragments, ref MftDataBytes, ref MftBitmapFragments, ref MftBitmapBytes,
                        Helper.BinaryReader(Buffer, (Int64)diskInfo.InodeToBytes(InodeNumber - BlockStart)),
                        diskInfo.BytesPerMftRecord);

                if (m_msDefragLib.Data.PhaseDone % 50 == 0)
                    ShowDebug(1, "Done: " + m_msDefragLib.Data.PhaseDone + "/" + m_msDefragLib.Data.PhaseTodo);
                InodeNumber++;
            }

            DateTime endTime = DateTime.Now;

            if (endTime > startTime)
            {
                ShowDebug(2, String.Format("  Analysis speed: {0:G} items per second",
                      (Int64)MaxInode * 1000 / (endTime - startTime).TotalMilliseconds));
            }

            using (m_msDefragLib.Data.Disk)
            {
                if (m_msDefragLib.Data.Running != RunningState.RUNNING)
                {
                    m_msDefragLib.DeleteItemTree(m_msDefragLib.Data.ItemTree);
                    m_msDefragLib.Data.ItemTree = null;
                    return false;
                }

                // Setup the ParentDirectory in all the items with the info in the InodeArray.
                for (Item = ItemTree.TreeSmallest(m_msDefragLib.Data.ItemTree); Item != null; Item = ItemTree.TreeNext(Item))
                {
                    Item.ParentDirectory = (ItemStruct)InodeArray.GetValue((Int64)Item.ParentInode);
                    if (Item.ParentInode == 5)
                        Item.ParentDirectory = null;
                }
            }
            return true;
        }
    }
}

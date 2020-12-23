﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UpdateFieldCodeGenerator.Formats
{
    public class WowPacketParserHandler : UpdateFieldHandlerBase
    {
        private static readonly string ModuleName = "V8_0_1_27101";
        private static readonly string Version = "V8_3_0_32861";

        public WowPacketParserHandler() : base(new StreamWriter("UpdateFieldsHandler.cs"), null)
        {
        }

        public override void BeforeStructures()
        {
            _source.WriteLine("using System.Collections;");
            _source.WriteLine("using System.Linq;");
            _source.WriteLine("using WowPacketParser.Enums;");
            _source.WriteLine("using WowPacketParser.Misc;");
            _source.WriteLine("using WowPacketParser.Parsing.Parsers;");
            _source.WriteLine("using WowPacketParser.Store.Objects.UpdateFields;");
            _source.WriteLine();
            _source.WriteLine($"namespace WowPacketParserModule.{ModuleName}.UpdateFields.{Version}");
            _source.WriteLine("{");
            _source.WriteLine("    public class UpdateFieldHandler : UpdateFieldsHandlerBase");
            _source.WriteLine("    {");
        }

        public override void AfterStructures()
        {
            _source.WriteLine("    }");
            _source.WriteLine("}");
        }

        public override void OnStructureBegin(Type structureType, ObjectType objectType, bool create, bool writeUpdateMasks)
        {
            base.OnStructureBegin(structureType, objectType, create, writeUpdateMasks);
            var structureName = RenameType(structureType);

            if (_create)
            {
                _header = new StreamWriter($"{structureName}.cs");
                _header.WriteLine("using WowPacketParser.Misc;");
                _header.WriteLine("using WowPacketParser.Store.Objects.UpdateFields;");
                _header.WriteLine();
                _header.WriteLine($"namespace WowPacketParserModule.{ModuleName}.UpdateFields.{Version}");
                _header.WriteLine("{");
                _header.WriteLine($"    public class {structureName} : I{structureName}");
                _header.WriteLine("    {");
            }

            var methodType = _isRoot ? "override" : "static";

            _indent = 2;
            if (_create)
            {
                if (_isRoot)
                    _source.WriteLine($"{GetIndent()}public {methodType} I{structureName} ReadCreate{structureName}(Packet packet, UpdateFieldFlag flags)");
                else
                    _source.WriteLine($"{GetIndent()}public {methodType} I{structureName} ReadCreate{structureName}(Packet packet)");
            }
            else
                _source.WriteLine($"{GetIndent()}public {methodType} I{structureName} ReadUpdate{structureName}(Packet packet, I{structureName} existingData)");

            _source.WriteLine($"{GetIndent()}{{");
            _indent = 3;
            if (_create)
                _source.WriteLine($"{GetIndent()}var data = new {structureName}();");
            else
            {
                _source.WriteLine($"{GetIndent()}var data = existingData as {structureName};");
                _source.WriteLine($"{GetIndent()}if (data == null)");
                _source.WriteLine($"{GetIndent()}    data = new {structureName}();");
            }
        }

        public override void OnStructureEnd(bool needsFlush, bool forceMaskMask)
        {
            if (_create)
            {
                _header.WriteLine("    }");
                _header.WriteLine("}");
                _header.WriteLine();
                _header.Close();
                _header = null;
            }

            if (needsFlush)
                _source.WriteLine($"{GetIndent()}packet.ResetBitReader();");

            if (!_create && _writeUpdateMasks)
            {
                ++_bitCounter;
                var maskBlocks = (_bitCounter + 31) / 32;
                _source.WriteLine($"{GetIndent()}var rawChangesMask = new int[{maskBlocks}];");
                if (maskBlocks > 1 || forceMaskMask)
                {
                    _source.WriteLine($"{GetIndent()}var rawMaskMask = new int[{(maskBlocks + 31) / 32}];");
                    if (maskBlocks >= 32)
                    {
                        _source.WriteLine($"{GetIndent()}for (var i = 0; i < {maskBlocks / 32}; ++i)");
                        _source.WriteLine($"{GetIndent()}    rawMaskMask[i] = packet.ReadInt32();");
                        if ((maskBlocks % 32) != 0)
                            _source.WriteLine($"{GetIndent()}rawMaskMask[{maskBlocks / 32}] = (int)packet.ReadBits({maskBlocks % 32});");
                    }
                    else
                        _source.WriteLine($"{GetIndent()}rawMaskMask[0] = (int)packet.ReadBits({maskBlocks});");

                    _source.WriteLine($"{GetIndent()}var maskMask = new BitArray(rawMaskMask);");
                    if (maskBlocks > 1)
                    {
                        _source.WriteLine($"{GetIndent()}for (var i = 0; i < {maskBlocks}; ++i)");
                        _source.WriteLine($"{GetIndent()}    if (maskMask[i])");
                        _source.WriteLine($"{GetIndent()}        rawChangesMask[i] = (int)packet.ReadBits(32);");
                    }
                    else
                    {
                        _source.WriteLine($"{GetIndent()}if (maskMask[0])");
                        _source.WriteLine($"{GetIndent()}    rawChangesMask[0] = (int)packet.ReadBits(32);");
                    }
                }
                else
                    _source.WriteLine($"{GetIndent()}rawChangesMask[0] = (int)packet.ReadBits({_bitCounter});");

                _source.WriteLine($"{GetIndent()}var changesMask = new BitArray(rawChangesMask);");
                _source.WriteLine();
            }

            PostProcessFieldWrites();

            if (!_create)
            {
                foreach (var dynamicChangesMaskType in _dynamicChangesMaskTypes)
                {
                    var typeName = RenameType(dynamicChangesMaskType);
                    _source.WriteLine($"{GetIndent()}var no{typeName}ChangesMask = packet.ReadBit();");
                }
            }

            List<FlowControlBlock> previousFlowControl = null;
            foreach (var (_, _, Write) in _fieldWrites)
                previousFlowControl = Write(previousFlowControl);

            _source.WriteLine($"{GetIndent()}return data;");

            _indent = 2;
            _source.WriteLine($"{GetIndent()}}}");
            _source.WriteLine();
            _source.Flush();
        }

        public override IReadOnlyList<FlowControlBlock> OnField(string name, UpdateField updateField, IReadOnlyList<FlowControlBlock> previousControlFlow)
        {
            name = RenameField(name);

            var flowControl = new List<FlowControlBlock>();
            if (_create && updateField.Flag != UpdateFieldFlag.None)
                flowControl.Add(new FlowControlBlock { Statement = $"if ((flags & {updateField.Flag.ToFlagsExpression(" | ", "UpdateFieldFlag.", "", "(", ")")}) != UpdateFieldFlag.None)" });

            var type = updateField.Type;
            var outputFieldName = name;
            var nextIndex = string.Empty;
            var declarationType = updateField.Type;
            var declarationSettable = true;
            var arrayLoopBlockIndex = -1;
            var indexLetter = 'i';
            if (type.IsArray)
            {
                flowControl.Add(new FlowControlBlock { Statement = $"for (var {indexLetter} = 0; {indexLetter} < {updateField.Size}; ++{indexLetter})" });
                outputFieldName += $"[{indexLetter}]";
                type = type.GetElementType();
                nextIndex += ", " + indexLetter;
                declarationSettable = false;
                arrayLoopBlockIndex = flowControl.Count;
                ++indexLetter;
            }
            if (typeof(DynamicUpdateField).IsAssignableFrom(type))
            {
                flowControl.Add(new FlowControlBlock { Statement = $"for (var {indexLetter} = 0; {indexLetter} < data.{outputFieldName}.Count; ++{indexLetter})" });
                if (!_create)
                    flowControl.Add(new FlowControlBlock { Statement = $"if (data.{outputFieldName}.UpdateMask[{indexLetter}])" });

                outputFieldName += $"[{indexLetter}]";
                type = type.GenericTypeArguments[0];
                nextIndex += ", " + indexLetter;
                declarationSettable = false;
                ++indexLetter;
            }
            if (typeof(BlzVectorField).IsAssignableFrom(type))
            {
                flowControl.Add(new FlowControlBlock { Statement = $"for (var {indexLetter} = 0; {indexLetter} < data.{outputFieldName}.Length; ++{indexLetter})" });
                outputFieldName += $"[{indexLetter}]";
                type = type.GenericTypeArguments[0];
                nextIndex += ", " + indexLetter;
                declarationType = type.MakeArrayType();
                ++indexLetter;
            }
            if (typeof(BlzOptionalField).IsAssignableFrom(type))
            {
                flowControl.Add(new FlowControlBlock { Statement = $"if (has{name})" });
                type = type.GenericTypeArguments[0];
                declarationType = type;
            }
            if (typeof(Bits).IsAssignableFrom(type))
            {
                declarationType = typeof(uint);
            }

            if (!_create && _writeUpdateMasks)
            {
                GenerateBitIndexConditions(updateField, name, flowControl, previousControlFlow, arrayLoopBlockIndex);
                if (name.EndsWith("is_initialized()"))
                    flowControl.RemoveAt(1); // bit generated but not checked for is_initialized
            }

            Type interfaceType = null;
            if (updateField.SizeForField != null)
            {
                type = (updateField.SizeForField.GetValue(null) as UpdateField).Type;
                type = type.GenericTypeArguments[0];
                interfaceType = TypeHandler.ConvertToInterfaces(type, rawName => RenameType(rawName));
            }

            RegisterDynamicChangesMaskFieldType(type);

            _fieldWrites.Add((name, false, (pcf) =>
            {
                WriteControlBlocks(_source, flowControl, pcf);
                WriteField(name, outputFieldName, type, updateField.BitSize, nextIndex, interfaceType);
                _indent = 3;
                return flowControl;
            }
            ));

            if (_create && updateField.SizeForField == null)
                WriteFieldDeclaration(name, updateField, declarationType, declarationSettable);

            return flowControl;
        }

        public override IReadOnlyList<FlowControlBlock> OnDynamicFieldSizeCreate(string name, UpdateField updateField, IReadOnlyList<FlowControlBlock> previousControlFlow)
        {
            name = RenameField(name);
            var flowControl = new List<FlowControlBlock>();
            if (_create && updateField.Flag != UpdateFieldFlag.None)
                flowControl.Add(new FlowControlBlock { Statement = $"if ((flags & {updateField.Flag.ToFlagsExpression(" | ", "UpdateFieldFlag.", "", "(", ")")}) != UpdateFieldFlag.None)" });

            var nameUsedToWrite = name;
            if (updateField.Type.IsArray)
            {
                flowControl.Add(new FlowControlBlock { Statement = $"for (var i = 0; i < {updateField.Size}; ++i)" });
                nameUsedToWrite += "[i]";
            }

            _fieldWrites.Add((name, true, (pcf) =>
            {
                WriteControlBlocks(_source, flowControl, pcf);
                if (updateField.BitSize > 0)
                    _source.WriteLine($"{GetIndent()}data.{nameUsedToWrite}.Resize(packet.ReadBits({updateField.BitSize}));");
                else
                    _source.WriteLine($"{GetIndent()}data.{nameUsedToWrite}.Resize(packet.ReadUInt32());");
                _indent = 3;
                return flowControl;
            }
            ));
            return flowControl;
        }

        public override IReadOnlyList<FlowControlBlock> OnDynamicFieldSizeUpdate(string name, UpdateField updateField, IReadOnlyList<FlowControlBlock> previousControlFlow)
        {
            name = RenameField(name);
            var flowControl = new List<FlowControlBlock>();
            if (_create && updateField.Flag != UpdateFieldFlag.None)
                flowControl.Add(new FlowControlBlock { Statement = $"if ((flags & {updateField.Flag.ToFlagsExpression(" | ", "UpdateFieldFlag.", "", "(", ")")}) != UpdateFieldFlag.None)" });

            var nameUsedToWrite = name;
            var arrayLoopBlockIndex = -1;
            if (updateField.Type.IsArray)
            {
                flowControl.Add(new FlowControlBlock { Statement = $"for (var i = 0; i < {updateField.Size}; ++i)" });
                nameUsedToWrite += "[i]";
                arrayLoopBlockIndex = flowControl.Count;
            }

            if (_writeUpdateMasks)
                GenerateBitIndexConditions(updateField, name, flowControl, previousControlFlow, arrayLoopBlockIndex);

            _fieldWrites.Add((name, true, (pcf) =>
            {
                WriteControlBlocks(_source, flowControl, pcf);
                var bitCountArgument = updateField.BitSize > 0 ? ", " + updateField.BitSize : "";
                _source.WriteLine($"{GetIndent()}data.{nameUsedToWrite}.ReadUpdateMask(packet{bitCountArgument});");
                _indent = 3;
                return flowControl;
            }
            ));
            return flowControl;
        }

        public override IReadOnlyList<FlowControlBlock> OnOptionalFieldInitCreate(string name, UpdateField updateField, IReadOnlyList<FlowControlBlock> previousControlFlow)
        {
            name = RenameField(name);
            var flowControl = new List<FlowControlBlock>();
            if (_create && updateField.Flag != UpdateFieldFlag.None)
                flowControl.Add(new FlowControlBlock { Statement = $"if ((flags & {updateField.Flag.ToFlagsExpression(" | ", "UpdateFieldFlag.", "", "(", ")")}) != UpdateFieldFlag.None)" });

            var nameUsedToWrite = name;
            if (updateField.Type.IsArray)
            {
                flowControl.Add(new FlowControlBlock { Statement = $"for (var i = 0; i < {updateField.Size}; ++i)" });
                nameUsedToWrite += "[i]";
            }

            _fieldWrites.Add((name, true, (pcf) =>
            {
                WriteControlBlocks(_source, flowControl, pcf);
                _source.WriteLine($"{GetIndent()}var has{name} = packet.ReadBit(\"Has{name}\");");
                _indent = 3;
                return flowControl;
            }
            ));
            return flowControl;
        }

        public override IReadOnlyList<FlowControlBlock> OnOptionalFieldInitUpdate(string name, UpdateField updateField, IReadOnlyList<FlowControlBlock> previousControlFlow)
        {
            name = RenameField(name);
            var flowControl = new List<FlowControlBlock>();

            var nameUsedToWrite = name;
            var arrayLoopBlockIndex = -1;
            if (updateField.Type.IsArray)
            {
                flowControl.Add(new FlowControlBlock { Statement = $"for (var i = 0; i < {updateField.Size}; ++i)" });
                nameUsedToWrite += "[i]";
                arrayLoopBlockIndex = flowControl.Count;
            }

            if (_writeUpdateMasks)
            {
                GenerateBitIndexConditions(updateField, name, flowControl, previousControlFlow, arrayLoopBlockIndex);
                flowControl.RemoveAt(1); // bit generated but not checked for is_initialized
            }

            _fieldWrites.Add((name, true, (pcf) =>
            {
                WriteControlBlocks(_source, flowControl, pcf);
                _source.WriteLine($"{GetIndent()}var has{name} = packet.ReadBit(\"Has{name}\");");
                _indent = 3;
                return flowControl;
            }
            ));
            return flowControl;
        }

        private void GenerateBitIndexConditions(UpdateField updateField, string name, List<FlowControlBlock> flowControl, IReadOnlyList<FlowControlBlock> previousControlFlow, int arrayLoopBlockIndex)
        {
            var newField = false;
            var nameForIndex = updateField.SizeForField != null ? RenameField(updateField.SizeForField.Name) : name;
            if (!_fieldBitIndex.TryGetValue(nameForIndex, out var bitIndex))
            {
                bitIndex = new List<int>();
                if (flowControl.Count == 0 || !FlowControlBlock.AreChainsAlmostEqual(previousControlFlow, flowControl))
                {
                    if (!updateField.Type.IsArray)
                    {
                        ++_nonArrayBitCounter;
                        if (_nonArrayBitCounter == 32)
                        {
                            _blockGroupBit = ++_bitCounter;
                            _nonArrayBitCounter = 1;
                        }
                    }

                    bitIndex.Add(++_bitCounter);

                    if (!updateField.Type.IsArray)
                        bitIndex.Add(_blockGroupBit);
                }
                else
                {
                    if (_previousFieldCounters == null || _previousFieldCounters.Count == 1)
                        throw new Exception("Expected previous field to have been an array");

                    bitIndex.Add(_previousFieldCounters[0]);
                }

                _fieldBitIndex[nameForIndex] = bitIndex;
                newField = true;
            }

            if (updateField.Type.IsArray)
            {
                flowControl.Insert(0, new FlowControlBlock { Statement = $"if (changesMask[{bitIndex[0]}])" });
                if (newField)
                {
                    bitIndex.AddRange(Enumerable.Range(_bitCounter + 1, updateField.Size));
                    _bitCounter += updateField.Size;
                }
                flowControl.Insert(arrayLoopBlockIndex + 1, new FlowControlBlock { Statement = $"if (changesMask[{bitIndex[1]} + i])" });
            }
            else
            {
                flowControl.Insert(0, new FlowControlBlock { Statement = $"if (changesMask[{_blockGroupBit}])" });
                flowControl.Insert(1, new FlowControlBlock { Statement = $"if (changesMask[{bitIndex[0]}])" });
            }

            _previousFieldCounters = bitIndex;
        }

        private void WriteField(string name, string outputFieldName, Type type, int bitSize, string nextIndex, Type interfaceType)
        {
            _source.Write(GetIndent());
            if (name.EndsWith("size()"))
            {
                outputFieldName = outputFieldName.Substring(0, outputFieldName.Length - 9);
                var interfaceName = RenameType(TypeHandler.GetFriendlyName(interfaceType));
                if (_create || !_isRoot)
                    _source.WriteLine($"data.{outputFieldName} = new {interfaceName}[packet.ReadUInt32()];");
                else
                    _source.WriteLine($"data.{outputFieldName} = Enumerable.Range(0, (int)packet.ReadBits(32)).Select(x => new {RenameType(TypeHandler.GetFriendlyName(type))}()).Cast<{interfaceName}>().ToArray();");
                return;
            }

            if (name.EndsWith("is_initialized()"))
            {
                outputFieldName = outputFieldName.Substring(0, outputFieldName.Length - 17);
                _source.WriteLine($"var has{outputFieldName} = packet.ReadBit(\"Has{outputFieldName}\");");
                return;
            }

            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Object:
                    if (type == typeof(WoWGuid))
                        _source.WriteLine($"data.{outputFieldName} = packet.ReadPackedGuid128();");
                    else if (type == typeof(Bits))
                        _source.WriteLine($"data.{outputFieldName} = packet.UnalignedReadInt({bitSize});");
                    else if (type == typeof(Vector2))
                        _source.WriteLine($"data.{outputFieldName} = packet.ReadVector2();");
                    else if (type == typeof(Quaternion))
                        _source.WriteLine($"data.{outputFieldName} = packet.ReadQuaternion();");
                    else if (_create)
                        _source.WriteLine($"data.{outputFieldName} = ReadCreate{RenameType(type)}(packet);");
                    else
                    {
                        if (_dynamicChangesMaskTypes.Contains(type.Name))
                        {
                            _source.WriteLine($"if (no{RenameType(type.Name)}ChangesMask)");
                            _source.WriteLine($"{GetIndent()}    data.{outputFieldName} = ReadCreate{RenameType(type)}(packet);");
                            _source.WriteLine($"{GetIndent()}else");
                            _source.WriteLine($"{GetIndent()}    data.{outputFieldName} = ReadUpdate{RenameType(type)}(packet, data.{outputFieldName} as {RenameType(type)});");

                        }
                        else
                            _source.WriteLine($"data.{outputFieldName} = ReadUpdate{RenameType(type)}(packet, data.{outputFieldName} as {RenameType(type)});");
                    }
                    break;
                case TypeCode.Boolean:
                    _source.WriteLine($"data.{outputFieldName} = packet.ReadBit();");
                    break;
                case TypeCode.SByte:
                    _source.WriteLine($"data.{outputFieldName} = packet.ReadSByte();");
                    break;
                case TypeCode.Byte:
                    _source.WriteLine($"data.{outputFieldName} = packet.ReadByte();");
                    break;
                case TypeCode.Int16:
                    _source.WriteLine($"data.{outputFieldName} = packet.ReadInt16();");
                    break;
                case TypeCode.UInt16:
                    _source.WriteLine($"data.{outputFieldName} = packet.ReadUInt16();");
                    break;
                case TypeCode.Int32:
                    _source.WriteLine($"data.{outputFieldName} = packet.ReadInt32();");
                    break;
                case TypeCode.UInt32:
                    _source.WriteLine($"data.{outputFieldName} = packet.ReadUInt32();");
                    break;
                case TypeCode.Int64:
                    _source.WriteLine($"data.{outputFieldName} = packet.ReadInt64();");
                    break;
                case TypeCode.UInt64:
                    _source.WriteLine($"data.{outputFieldName} = packet.ReadUInt64();");
                    break;
                case TypeCode.Single:
                    _source.WriteLine($"data.{outputFieldName} = packet.ReadSingle();");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(type));
            }
        }

        private void WriteFieldDeclaration(string name, UpdateField updateField, Type declarationType, bool declarationSettable)
        {
            declarationType = TypeHandler.ConvertToInterfaces(declarationType, rawName => RenameType(rawName));
            _header.Write($"        public {TypeHandler.GetFriendlyName(declarationType)} {name} {{ get;{(declarationSettable ? " set;" : "")} }}");
            if (typeof(DynamicUpdateField).IsAssignableFrom(updateField.Type))
                _header.Write($" = new {TypeHandler.GetFriendlyName(declarationType)}();");
            else if (updateField.Type.IsArray)
            {
                var typeFormat = TypeHandler.GetFriendlyName(declarationType.GetElementType());
                _header.Write($" = new {typeFormat}[{updateField.Size}]");
                if (typeof(DynamicUpdateField).IsAssignableFrom(updateField.Type.GetElementType()))
                {
                    _header.Write($" {{ ");
                    for (var i = 0; i < updateField.Size; ++i)
                    {
                        if (i != 0)
                            _header.Write(", ");

                        _header.Write($"new {typeFormat}()");
                    }
                    _header.Write(" }");
                }
                _header.Write(";");
            }

            _header.WriteLine();
        }

        protected override string RenameType(Type type)
        {
            return RenameType(type.Name);
        }

        private string RenameType(string name)
        {
            if (name.StartsWith("CG") && char.IsUpper(name[2]))
                name = name.Substring(2);
            if (name.EndsWith("_C"))
                name = name.Substring(0, name.Length - 2);
            if (name.StartsWith("JamMirror"))
                name = name.Substring(9);
            return name;
        }

        protected override string RenameField(string name)
        {
            name = name.Replace("m_", "");
            return char.ToUpperInvariant(name[0]) + name.Substring(1);
        }

        public override void FinishControlBlocks(IReadOnlyList<FlowControlBlock> previousControlFlow)
        {
            _fieldWrites.Add((string.Empty, false, (pcf) =>
            {
                FinishControlBlocks(_source, pcf);
                return new List<FlowControlBlock>();
            }));
        }

        public override void FinishBitPack()
        {
            _fieldWrites.Add((string.Empty, false, (pcf) =>
            {
                _source.WriteLine($"{GetIndent()}packet.ResetBitReader();");
                return new List<FlowControlBlock>();
            }
            ));
        }
    }
}

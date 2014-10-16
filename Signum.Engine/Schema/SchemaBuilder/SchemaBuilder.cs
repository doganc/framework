using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Data;
using Signum.Entities;
using Signum.Utilities;
using Signum.Utilities.Reflection;
using Signum.Utilities.ExpressionTrees;
using System.Diagnostics;
using Signum.Entities.Reflection;
using System.Linq.Expressions;
using System.Runtime.Remoting.Contexts;
using Signum.Engine.Linq;
using Signum.Entities.Basics;
using Signum.Engine.Basics;
using Signum.Utilities.DataStructures;
using System.Threading;

namespace Signum.Engine.Maps
{
    public class SchemaBuilder
    {
        Schema schema;
        public SchemaSettings Settings
        {
            get { return schema.Settings; }
        }

        public SchemaBuilder()
        {
            schema = new Schema(new SchemaSettings());
            Include<TypeDN>();
            Settings.CanOverrideAttributes = MixinDeclarations.CanAddMixins = t => schema.Tables.ContainsKey(t) ? "{0} is already included in the Schema".Formato(t.TypeName()) : null;
        }

        protected SchemaBuilder(Schema schema)
        {
            this.schema = schema;
        }

        public SchemaBuilder(SchemaSettings settings)
        {
            schema = new Schema(settings);
        }

        public Schema Schema
        {
            get { return schema; }
        }


        public UniqueIndex AddUniqueIndex<T>(Expression<Func<T, object>> fields) where T : Entity
        {
            return AddUniqueIndex<T>(fields, null);
        }

        public UniqueIndex AddUniqueIndex<T>(Expression<Func<T, object>> fields, Expression<Func<T, bool>> where) where T : Entity
        {
            var table = Schema.Table<T>();

            IColumn[] columns = Split(table, fields);

            var index = AddUniqueIndex(table, columns);

            if (where != null)
                index.Where = IndexWhereExpressionVisitor.GetIndexWhere(where, table);

            return index;
        }

        public UniqueIndex AddUniqueIndexMList<T, V>(Expression<Func<T, MList<V>>> toMList, Expression<Func<MListElement<T, V>, object>> fields)
           where T : Entity
        {
            return AddUniqueIndexMList(toMList, fields, null);
        }

        public UniqueIndex AddUniqueIndexMList<T, V>(Expression<Func<T, MList<V>>> toMList, Expression<Func<MListElement<T, V>, object>> fields, Expression<Func<MListElement<T, V>, bool>> where)
            where T : Entity
        {
            TableMList table = ((FieldMList)Schema.FindField(Schema.Table(typeof(T)), Reflector.GetMemberList(toMList))).TableMList;

            IColumn[] columns = Split(table, fields);

            var index = AddUniqueIndex(table, columns);

            if (where != null)
                index.Where = IndexWhereExpressionVisitor.GetIndexWhere(where, table);

            return index;
        }

        IColumn[] Split<T>(IFieldFinder finder, Expression<Func<T, object>> columns)
        {
            if (columns == null)
                throw new ArgumentNullException("columns");

            if (columns.Body.NodeType == ExpressionType.New)
            {
                return (from a in ((NewExpression)columns.Body).Arguments
                        from c in GetColumns<T>(finder, Expression.Lambda<Func<T, object>>(Expression.Convert(a, typeof(object)), columns.Parameters))
                        select c).ToArray();
            }

            return GetColumns<T>(finder, columns);
        }

        static IColumn[] GetColumns<T>(IFieldFinder finder, Expression<Func<T, object>> field)
        {
            Type type = RemoveCasting(ref field);

            Field f = Schema.FindField(finder, Reflector.GetMemberList(field));

            if (type != null)
            {
                var ib = f as FieldImplementedBy;
                if (ib == null)
                    throw new InvalidOperationException("Casting only supported for {0}".Formato(typeof(FieldImplementedBy).Name));

                return (from ic in ib.ImplementationColumns
                        where type.IsAssignableFrom(ic.Key)
                        select (IColumn)ic.Value).ToArray();
            }

            return Index.GetColumnsFromFields(f);
        }

        static Type RemoveCasting<T>(ref Expression<Func<T, object>> field)
        {
            var body = field.Body;

            if (body.NodeType == ExpressionType.Convert && body.Type == typeof(object))
                body = ((UnaryExpression)body).Operand;

            Type type = null;
            if ((body.NodeType == ExpressionType.Convert || body.NodeType == ExpressionType.TypeAs) &&
                body.Type != typeof(object))
            {
                type = body.Type;
                body = ((UnaryExpression)body).Operand;
            }

            field = Expression.Lambda<Func<T, object>>(Expression.Convert(body, typeof(object)), field.Parameters);
            return type;
        }

        public UniqueIndex AddUniqueIndex(ITable table, Field[] fields)
        {
            var index = new UniqueIndex(table, Index.GetColumnsFromFields(fields));
            AddIndex(index);
            return index;
        }

        public UniqueIndex AddUniqueIndex(ITable table, IColumn[] columns)
        {
            var index = new UniqueIndex(table, columns);
            AddIndex(index);
            return index;
        }

        private void AddIndex(Index index)
        {
            ITable table = index.Table;

            if (table.MultiColumnIndexes == null)
                table.MultiColumnIndexes = new List<Index>();

            table.MultiColumnIndexes.Add(index);
        }

        public Table Include<T>() where T : Entity
        {
            return Include(typeof(T), null);
        }

        public virtual Table Include(Type type)
        {
            return Include(type, null);
        }

        internal protected virtual Table Include(Type type, PropertyRoute route)
        {
            Table result;
            if (schema.Tables.TryGetValue(type, out result))
                return result;

            using (HeavyProfiler.LogNoStackTrace("Include", () => type.TypeName()))
            {
                if (type.IsAbstract)
                    throw new InvalidOperationException(route.Try(r => "Error on field {0}: ".Formato(r)) + "Impossible to include in the Schema the type {0} because is abstract".Formato(type));

                if (!Reflector.IsEntity(type))
                    throw new InvalidOperationException(route.Try(r => "Error on field {0}: ".Formato(r)) + "Impossible to include in the Schema the type {0} because is not and Entity".Formato(type));

                foreach (var t in type.Follow(a => a.BaseType))
                    if (!t.IsSerializable)
                        throw new InvalidOperationException("Type {0} is not marked as serializable".Formato(t.TypeName()));

                result = new Table(type);

                schema.Tables.Add(type, result);

                string name = schema.Settings.desambiguatedNames.TryGetC(type) ?? Reflector.CleanTypeName(EnumEntity.Extract(type) ?? type);

                if (schema.NameToType.ContainsKey(name))
                    throw new InvalidOperationException(route.Try(r => "Error on field {0}: ".Formato(r)) + "Two types have the same cleanName, desambiguate using Schema.Current.Settings.Desambiguate method: \r\n {0}\r\n {1}".Formato(schema.NameToType[name].FullName, type.FullName));

                schema.NameToType[name] = type;
                schema.TypeToName[type] = name;

                Complete(result);
                return result;
            }
        }

        void Complete(Table table)
        {
            Type type = table.Type;
            table.IdentityBehaviour = (Settings.TypeAttributes<PrimaryKeyAttribute>(type) ?? Settings.DefaultPrimaryKeyAttribute).IdentityBehaviour;
            table.Name = GenerateTableName(type, Settings.TypeAttributes<TableNameAttribute>(type));
            table.CleanTypeName = GenerateCleanTypeName(type);
            table.Fields = GenerateFields(PropertyRoute.Root(type), table, NameSequence.Void, forceNull: false, inMList: false);
            table.Mixins = GenerateMixins(PropertyRoute.Root(type), table, NameSequence.Void);
            table.GenerateColumns();
        }

        private Dictionary<Type, FieldMixin> GenerateMixins(PropertyRoute propertyRoute, Table table, NameSequence nameSequence)
        {
            Dictionary<Type, FieldMixin> mixins = null;
            foreach (var t in MixinDeclarations.GetMixinDeclarations(table.Type))
            {
                if (mixins == null)
                    mixins = new Dictionary<Type, FieldMixin>();

                mixins.Add(t, this.GenerateFieldMixin(propertyRoute.Add(t), nameSequence, table));
            }

            return mixins;
        }

        HashSet<string> loadedModules = new HashSet<string>();
        public bool NotDefined(MethodBase methodBase)
        {
            var should = methodBase.DeclaringType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
             .Where(m => !m.HasAttribute<MethodExpanderAttribute>())
             .Select(m => m.GetCustomAttribute<ExpressionFieldAttribute>().Try(a => a.Name) ?? m.Name + "Expression").ToList();

            var fields = methodBase.DeclaringType.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => f.Name.EndsWith("Expression") && f.FieldType.IsInstantiationOf(typeof(Expression<>)));

            foreach (var f in fields)
                should.Where(a => a == f.Name).SingleEx(() => "Methods for {0}".Formato(f.Name));


            return loadedModules.Add(methodBase.DeclaringType.FullName + "." + methodBase.Name);
        }

        public void AssertDefined(MethodBase methodBase)
        {
            string name = methodBase.DeclaringType.FullName + "." + methodBase.Name;

            if (!loadedModules.Contains(name))
                throw new ApplicationException("Call {0} first".Formato(name));
        }

        #region Field Generator


        protected Dictionary<string, EntityField> GenerateFields(PropertyRoute root, ITable table, NameSequence preName, bool forceNull, bool inMList)
        {
            Dictionary<string, EntityField> result = new Dictionary<string, EntityField>();
            var type = root.Type;

            if (type.IsEntity())
            {
                {
                    PropertyRoute route = root.Add(fiId);

                    Field field = GenerateField(table, route, preName, forceNull, inMList);

                    result.Add(fiId.Name, new EntityField(type, fiId) { Field = field });
                }

                TicksColumnAttribute t = type.GetCustomAttribute<TicksColumnAttribute>();
                if (t == null || t.HasTicks)
                {
                    PropertyRoute route = root.Add(fiTicks);

                    Field field = GenerateField(table, route, preName, forceNull, inMList);

                    result.Add(fiTicks.Name, new EntityField(type, fiTicks) { Field = field });
                }

                Expression exp = ExpressionCleaner.GetFieldExpansion(type, EntityExpression.ToStringMethod);

                if (exp == null)
                {
                    PropertyRoute route = root.Add(fiToStr);

                    Field field = GenerateField(table, route, preName, forceNull, inMList);

                    if (result.ContainsKey(fiToStr.Name))
                        throw new InvalidOperationException("Duplicated field with name {0} on {1}, shadowing not supported".Formato(fiToStr.Name, type.TypeName()));

                    result.Add(fiToStr.Name, new EntityField(type, fiToStr) { Field = field });
                }
            }

            foreach (FieldInfo fi in Reflector.InstanceFieldsInOrder(type))
            {
                PropertyRoute route = root.Add(fi);

                if (Settings.FieldAttribute<IgnoreAttribute>(route) == null)
                {
                    if (Reflector.TryFindPropertyInfo(fi) == null && !fi.IsPublic && !fi.HasAttribute<FieldWithoutPropertyAttribute>())
                        throw new InvalidOperationException("Field '{0}' of type '{1}' has no property".Formato(fi.Name, type.Name));

                    Field field = GenerateField(table, route, preName, forceNull, inMList);

                    if (result.ContainsKey(fi.Name))
                        throw new InvalidOperationException("Duplicated field with name '{0}' on '{1}', shadowing not supported".Formato(fi.Name, type.TypeName()));

                    result.Add(fi.Name, new EntityField(type, fi) { Field = field });
                }
            }

            return result;
        }

        static readonly FieldInfo fiToStr = ReflectionTools.GetFieldInfo((Entity o) => o.toStr);
        static readonly FieldInfo fiTicks = ReflectionTools.GetFieldInfo((Entity o) => o.ticks);
        static readonly FieldInfo fiId = ReflectionTools.GetFieldInfo((Entity o) => o.id);

        protected virtual Field GenerateField(ITable table, PropertyRoute route, NameSequence preName, bool forceNull, bool inMList)
        {
            //fieldType: Va variando segun se entra en colecciones o contenidos
            //fi.Type: el tipo del campo asociado

            KindOfField kof = GetKindOfField(route).ThrowIfNull("Field {0} of type {1} has no database representation".Formato(route, route.Type.Name));

            if(kof == KindOfField.MList && inMList)
                throw new InvalidOperationException("Field {0} of type {1} can not be neasted in another MList".Formato(route, route.Type.TypeName(), kof));

            //field name generation 
            NameSequence name = preName;
            if (route.PropertyRouteType != PropertyRouteType.MListItems)
                name = name.Add(GenerateFieldName(route, kof));
            else if (kof == KindOfField.Enum || kof == KindOfField.Reference)
                name = name.Add(GenerateMListFieldName(route, kof));

            switch (kof)
            {
                case KindOfField.PrimaryKey:
                    return GenerateFieldPrimaryKey((Table)table, route, name);
                case KindOfField.Ticks:
                    return GenerateFieldTicks((Table)table, route, name);
                case KindOfField.Value:
                    return GenerateFieldValue(table, route, name, forceNull);
                case KindOfField.Reference:
                    {
                        Implementations at = Settings.GetImplementations(route);
                        if (at.IsByAll)
                            return GenerateFieldImplementedByAll(route, table, name, forceNull);
                        else if (at.Types.Only() == route.Type.CleanType())
                            return GenerateFieldReference(table, route, name, forceNull);
                        else
                            return GenerateFieldImplementedBy(table, route, name, forceNull, at.Types);
                    }
                case KindOfField.Enum:
                    return GenerateFieldEnum(table, route, name, forceNull);
                case KindOfField.Embedded:
                    return GenerateFieldEmbedded(table, route, name, forceNull, inMList);
                case KindOfField.MList:
                    return GenerateFieldMList((Table)table, route, name);
                default:
                    throw new NotSupportedException(EngineMessage.NoWayOfMappingType0Found.NiceToString().Formato(route.Type));
            }
        }

        public enum KindOfField
        {
            PrimaryKey,
            Ticks,
            Value,
            Reference,
            Enum,
            Embedded,
            MList,
        }

        protected virtual KindOfField? GetKindOfField(PropertyRoute route)
        {
            if (route.FieldInfo != null && ReflectionTools.FieldEquals(route.FieldInfo, fiId))
                return KindOfField.PrimaryKey;

            if (route.FieldInfo != null && ReflectionTools.FieldEquals(route.FieldInfo, fiTicks))
                return KindOfField.Ticks;

            if (Settings.GetSqlDbType(Settings.FieldAttribute<SqlDbTypeAttribute>(route), route.Type) != null)
                return KindOfField.Value;

            if (route.Type.UnNullify().IsEnum)
                return KindOfField.Enum;

            if (Reflector.IsIEntity(Lite.Extract(route.Type) ?? route.Type))
                return KindOfField.Reference;

            if (Reflector.IsEmbeddedEntity(route.Type))
                return KindOfField.Embedded;

            if (Reflector.IsMList(route.Type))
                return KindOfField.MList;

            return null;
        }

        protected virtual Field GenerateFieldPrimaryKey(Table table, PropertyRoute route, NameSequence name)
        {
            var attr = Settings.TypeAttributes<PrimaryKeyAttribute>(table.Type) ?? Settings.DefaultPrimaryKeyAttribute;

            PrimaryKey.PrimaryKeyType.SetDefinition(table.Type, attr.Type);

            SqlDbTypePair pair = Settings.GetSqlDbType(attr, attr.Type);

            return table.PrimaryKey = new FieldPrimaryKey(route.Type, table)
            {
                Name = attr.Name,
                Type = attr.Type,
                SqlDbType = pair.SqlDbType,
                UserDefinedTypeName = pair.UserDefinedTypeName,
                Default = attr.Default,
                Identity = attr.Identity,
            };
        }


        protected virtual FieldValue GenerateFieldTicks(Table table, PropertyRoute route, NameSequence name)
        {
            var ticksAttr = Settings.TypeAttributes<TicksColumnAttribute>(table.Type);

            if (ticksAttr != null && !ticksAttr.HasTicks)
                throw new InvalidOperationException("HastTicks is false");

            Type type = ticksAttr.Try(t => t.Type) ?? route.Type;

            SqlDbTypePair pair = Settings.GetSqlDbType(ticksAttr, type);

            return table.Ticks = new FieldTicks(route.Type)
            {
                Type = type,
                Name = ticksAttr.Try(a=>a.Name) ?? name.ToString(),
                SqlDbType = pair.SqlDbType,
                UserDefinedTypeName = pair.UserDefinedTypeName,
                Nullable = false,
                Size = Settings.GetSqlSize(ticksAttr, pair.SqlDbType),
                Scale = Settings.GetSqlScale(ticksAttr, pair.SqlDbType),
                Default = ticksAttr.Try(a => a.Default),
            };
        }

        protected virtual FieldValue GenerateFieldValue(ITable table, PropertyRoute route, NameSequence name, bool forceNull)
        {
            var att = Settings.FieldAttribute<SqlDbTypeAttribute>(route);

            SqlDbTypePair pair = Settings.GetSqlDbType(att, route.Type);

            return new FieldValue(route.Type)
            {
                Name = name.ToString(),
                SqlDbType = pair.SqlDbType,
                UserDefinedTypeName = pair.UserDefinedTypeName,
                Nullable = Settings.IsNullable(route, forceNull),
                Size = Settings.GetSqlSize(att, pair.SqlDbType),
                Scale = Settings.GetSqlScale(att, pair.SqlDbType),
                Default = att.Try(a=>a.Default),
            }.Do(f => f.UniqueIndex = f.GenerateUniqueIndex(table, Settings.FieldAttribute<UniqueIndexAttribute>(route)));
        }

        protected virtual FieldEnum GenerateFieldEnum(ITable table, PropertyRoute route, NameSequence name, bool forceNull)
        {
            Type cleanEnum = route.Type.UnNullify();

            var referenceTable = Include(EnumEntity.Generate(cleanEnum), route);

            return new FieldEnum(route.Type)
            {
                Name = name.ToString(),
                Nullable = Settings.IsNullable(route, forceNull),
                IsLite = false,
                ReferenceTable = referenceTable,
                AvoidForeignKey = Settings.FieldAttribute<AvoidForeignKeyAttribute>(route) != null,
            }.Do(f => f.UniqueIndex = f.GenerateUniqueIndex(table, Settings.FieldAttribute<UniqueIndexAttribute>(route)));
        }

        protected virtual FieldReference GenerateFieldReference(ITable table, PropertyRoute route, NameSequence name, bool forceNull)
        {
            var referenceTable = Include(Lite.Extract(route.Type) ?? route.Type, route);

            var nullable = Settings.IsNullable(route, forceNull);

            return new FieldReference(route.Type)
            {
                Name = name.ToString(),
                Nullable = nullable,
                IsLite = route.Type.IsLite(),
                ReferenceTable = referenceTable,
                AvoidForeignKey = Settings.FieldAttribute<AvoidForeignKeyAttribute>(route) != null,
                AvoidExpandOnRetrieving = Settings.FieldAttribute<AvoidExpandQueryAttribute>(route) != null
            }.Do(f => f.UniqueIndex = f.GenerateUniqueIndex(table, Settings.FieldAttribute<UniqueIndexAttribute>(route)));
        }

        protected virtual FieldImplementedBy GenerateFieldImplementedBy(ITable table, PropertyRoute route, NameSequence name, bool forceNull, IEnumerable<Type> types)
        {
            Type cleanType = Lite.Extract(route.Type) ?? route.Type;
            string errors = types.Where(t => !cleanType.IsAssignableFrom(t)).ToString(t => t.TypeName(), ", ");
            if (errors.Length != 0)
                throw new InvalidOperationException("Type {0} do not implement {1}".Formato(errors, cleanType));

            bool nullable = Settings.IsNullable(route, forceNull) || types.Count() > 1;

            CombineStrategy strategy = Settings.FieldAttribute<CombineStrategyAttribute>(route).Try(s => s.Strategy) ?? CombineStrategy.Case;

            bool avoidForeignKey = Settings.FieldAttribute<AvoidForeignKeyAttribute>(route) != null;

            return new FieldImplementedBy(route.Type)
            {
                SplitStrategy = strategy,
                ImplementationColumns = types.ToDictionary(t => t, t => new ImplementationColumn
                {
                    ReferenceTable = Include(t, route),
                    Name = name.Add(TypeLogic.GetCleanName(t)).ToString(),
                    Nullable = nullable,
                    AvoidForeignKey = avoidForeignKey,
                }),
                IsLite = route.Type.IsLite(),
                AvoidExpandOnRetrieving = Settings.FieldAttribute<AvoidExpandQueryAttribute>(route) != null
            }.Do(f => f.UniqueIndex = f.GenerateUniqueIndex(table, Settings.FieldAttribute<UniqueIndexAttribute>(route)));
        }

        protected virtual FieldImplementedByAll GenerateFieldImplementedByAll(PropertyRoute route, ITable table, NameSequence preName, bool forceNull)
        {
            bool nullable = Settings.IsNullable(route, forceNull);

            return new FieldImplementedByAll(route.Type)
            {
                Column = new ImplementationStringColumn
                {
                    Name = preName.ToString(),
                    Nullable = nullable,
                    Size = Settings.DefaultImplementedBySize,
                },
                ColumnType = new ImplementationColumn
                {
                    Name = preName.Add("Type").ToString(),
                    Nullable = nullable,
                    ReferenceTable = Include(typeof(TypeDN), route),
                    AvoidForeignKey = Settings.FieldAttribute<AvoidForeignKeyAttribute>(route) != null,
                },
                IsLite = route.Type.IsLite(),
                AvoidExpandOnRetrieving = Settings.FieldAttribute<AvoidExpandQueryAttribute>(route) != null
            }.Do(f => f.UniqueIndex = f.GenerateUniqueIndex(table, Settings.FieldAttribute<UniqueIndexAttribute>(route)));
        }

        protected virtual FieldMList GenerateFieldMList(Table table, PropertyRoute route, NameSequence name)
        {
            Type elementType = route.Type.ElementType();

            if (table.Ticks == null)
                throw new InvalidOperationException("Type '{0}' has field '{1}' but does not Ticks. MList require concurrency control.".Formato(route.Parent.Type.TypeName(), route.FieldInfo.FieldName()));

            var orderAttr = Settings.FieldAttribute<PreserveOrderAttribute>(route);

            FieldValue order = null;
            if (orderAttr != null)
            {
                var pair = Settings.GetSqlDbTypePair(typeof(int));

                order = new FieldValue(typeof(int))
                {
                    Name = orderAttr.Name ?? "Order",
                    SqlDbType = pair.SqlDbType,
                    UserDefinedTypeName = pair.UserDefinedTypeName,
                    Nullable = false,
                    Size = Settings.GetSqlSize(orderAttr, pair.SqlDbType),
                    Scale = Settings.GetSqlScale(orderAttr, pair.SqlDbType),
                };
            }

            var keyAttr = Settings.FieldAttribute<PrimaryKeyAttribute>(route) ?? Settings.DefaultPrimaryKeyAttribute;
            TableMList.PrimaryKeyColumn primaryKey;
            {
                var pair = Settings.GetSqlDbType(keyAttr, keyAttr.Type);

                primaryKey = new TableMList.PrimaryKeyColumn
                {
                    Name = keyAttr.Name,
                    Type = keyAttr.Type,
                    SqlDbType = pair.SqlDbType,
                    UserDefinedTypeName = pair.UserDefinedTypeName,
                    Default = keyAttr.Default,
                    Identity = keyAttr.Identity,
                };
            }

            TableMList relationalTable = new TableMList(route.Type)
            {
                Name = GenerateTableNameCollection(table, name, Settings.FieldAttribute<TableNameAttribute>(route)),
                PrimaryKey = primaryKey,
                BackReference = new FieldReference(table.Type)
                {
                    Name = GenerateBackReferenceName(table.Type, Settings.FieldAttribute<BackReferenceColumnNameAttribute>(route)),
                    ReferenceTable = table,
                    AvoidForeignKey = Settings.FieldAttribute<AvoidForeignKeyAttribute>(route) != null,
                },
                Order = order,
            };

            relationalTable.Field = GenerateField(relationalTable, route.Add("Item"), NameSequence.Void, forceNull: false, inMList: true);

            relationalTable.GenerateColumns();

            return new FieldMList(route.Type)
            {
                TableMList = relationalTable,
            };
        }

        protected virtual FieldEmbedded GenerateFieldEmbedded(ITable table, PropertyRoute route, NameSequence name, bool forceNull, bool inMList)
        {
            bool nullable = Settings.IsNullable(route, false);

            return new FieldEmbedded(route.Type)
            {
                HasValue = nullable ? new FieldEmbedded.EmbeddedHasValueColumn() { Name = name.Add("HasValue").ToString() } : null,
                EmbeddedFields = GenerateFields(route, table, name, forceNull: nullable || forceNull, inMList: inMList)
            };
        }

        protected virtual FieldMixin GenerateFieldMixin(PropertyRoute route, NameSequence name, Table table)
        {
            return new FieldMixin(route.Type, table)
            {
                Fields = GenerateFields(route, table, name, forceNull: false, inMList: false)
            };
        }
        #endregion

        #region Names

        public virtual string GenerateCleanTypeName(Type type)
        {
            type = CleanType(type);

            CleanTypeNameAttribute ctn = type.GetCustomAttribute<CleanTypeNameAttribute>();
            if (ctn != null)
                return ctn.Name;

            return Reflector.CleanTypeName(type);
        }

        protected static Type CleanType(Type type)
        {
            type = Lite.Extract(type) ?? type;
            type = EnumEntity.Extract(type) ?? type;
            return type;
        }

        public virtual ObjectName GenerateTableName(Type type, TableNameAttribute tn)
        {
            SchemaName sn = tn != null ? GetSchemaName(tn) : SchemaName.Default;

            return new ObjectName(sn, tn.Try(a => a.Name) ?? CleanType(type).Name);
        }

        private SchemaName GetSchemaName(TableNameAttribute tn)
        {
            ServerName server = tn.ServerName == null ? null : new ServerName(tn.ServerName);
            DatabaseName dataBase = tn.DatabaseName == null && server == null ? null : new DatabaseName(server, tn.ServerName);
            SchemaName schema = tn.SchemaName == null && dataBase == null ? SchemaName.Default : new SchemaName(dataBase, tn.SchemaName);
            return schema;
        }

        public virtual ObjectName GenerateTableNameCollection(Table table, NameSequence name, TableNameAttribute tn)
        {
            SchemaName sn = tn != null ? GetSchemaName(tn) : SchemaName.Default;

            return new ObjectName(sn, tn.Try(a => a.Name) ?? (table.Name.Name + name.ToString()));
        }

        public virtual string GenerateMListFieldName(PropertyRoute route, KindOfField kindOfField)
        {
            ColumnNameAttribute vc = Settings.FieldAttribute<ColumnNameAttribute>(route);
            if (vc != null && vc.Name.HasText())
                return vc.Name;

            Type type = Lite.Extract(route.Type) ?? route.Type;

            switch (kindOfField)
            {
                case KindOfField.Value:
                case KindOfField.Embedded:
                    return type.Name.FirstUpper();
                case KindOfField.Enum:
                case KindOfField.Reference:
                    return "id" + CleanType(type).Name;
                default:
                    throw new InvalidOperationException("No field name for type {0} defined".Formato(type));
            }
        }

        public virtual string GenerateFieldName(PropertyRoute route, KindOfField kindOfField)
        {
            ColumnNameAttribute vc = Settings.FieldAttribute<ColumnNameAttribute>(route);
            if (vc != null && vc.Name.HasText())
                return vc.Name;

            string name = Reflector.PropertyName(route.FieldInfo.Name);

            switch (kindOfField)
            {
                case KindOfField.PrimaryKey:
                case KindOfField.Ticks:
                case KindOfField.Value:
                case KindOfField.Embedded:
                case KindOfField.MList:  //se usa solo para el nombre de la tabla 
                    return name;
                case KindOfField.Reference:
                case KindOfField.Enum:
                    return "id" + name;
                default:
                    throw new InvalidOperationException("No name for {0} defined".Formato(route.FieldInfo.Name));
            }
        }

        public virtual string GenerateBackReferenceName(Type type, BackReferenceColumnNameAttribute attribute)
        {
            return attribute.Try(a => a.Name) ?? "idParent";
        }
        #endregion


        List<WhenIncludedPair> whens = new List<WhenIncludedPair>();

        public void WhenIncluded<T1>(Action action)
            where T1 : Entity
        {
            WhenIncluded(new[] { typeof(T1) }, action);
        }

        public void WhenIncluded<T1, T2>(Action action)
            where T1 : Entity
            where T2 : Entity
        {
            WhenIncluded(new[] { typeof(T1), typeof(T2) }, action);
        }

        public void WhenIncluded<T1, T2, T3>(Action action)
            where T1 : Entity
            where T2 : Entity
            where T3 : Entity
        {
            WhenIncluded(new[] { typeof(T1), typeof(T2), typeof(T3) }, action);
        }

        public void WhenIncluded(Type[] types, Action action)
        {
            whens.Add(new WhenIncludedPair
            {
                Action = action,
                RegisteredTypes = types ?? new Type[0],
            });
        }

        public void ExecuteWhenIncluded()
        {
            foreach (var item in whens)
            {
                if (item.RegisteredTypes.All(t => Schema.Tables.ContainsKey(t)))
                    item.Action();
            }

            whens = null;
        }

        class WhenIncludedPair
        {
            public Action Action;
            public Type[] RegisteredTypes;
        }

        GlobalLazyManager GlobalLazyManager = new GlobalLazyManager();

        public void SwitchGlobalLazyManager(GlobalLazyManager manager)
        {
            GlobalLazyManager.AsserNotUsed();
            GlobalLazyManager = manager;
        }

        public ResetLazy<T> GlobalLazy<T>(Func<T> func, InvalidateWith invalidateWith) where T : class
        {
            var result = Signum.Engine.GlobalLazy.WithoutInvalidations(() =>
            {
                GlobalLazyManager.OnLoad(this, invalidateWith);

                return func();
            });

            GlobalLazyManager.AttachInvalidations(this, invalidateWith, (sender, args) => result.Reset());

            return result;
        }
    }

    public class GlobalLazyManager
    {
        bool isUsed = false;

        public void AsserNotUsed()
        {
            if (isUsed)
                throw new InvalidOperationException("GlobalLazyManager has already been used");
        }

        public virtual void AttachInvalidations(SchemaBuilder sb, InvalidateWith invalidateWith, EventHandler invalidate)
        {
            isUsed = true;

            Action onInvalidation = () =>
            {
                if (Transaction.InTestTransaction)
                {
                    invalidate(this, null);
                    Transaction.Rolledback += dic => invalidate(this, null);
                }

                Transaction.PostRealCommit += dic => invalidate(this, null);
            };

            Schema schema = sb.Schema;

            foreach (var type in invalidateWith.Types)
            {
                giAttachInvalidations.GetInvoker(type)(schema, onInvalidation);
            }

            var dependants = DirectedGraph<Table>.Generate(invalidateWith.Types.Select(t => schema.Table(t)), t => t.DependentTables().Select(kvp => kvp.Key)).Select(t => t.Type).ToHashSet();
            dependants.ExceptWith(invalidateWith.Types);

            foreach (var type in dependants)
            {
                giAttachInvalidationsDependant.GetInvoker(type)(schema, onInvalidation);
            }
        }


        static GenericInvoker<Action<Schema, Action>> giAttachInvalidationsDependant = new GenericInvoker<Action<Schema, Action>>((s, a) => AttachInvalidationsDependant<Entity>(s, a));
        static void AttachInvalidationsDependant<T>(Schema s, Action action) where T : Entity
        {
            var ee = s.EntityEvents<T>();

            ee.Saving += e =>
            {
                if (!e.IsNew && e.IsGraphModified)
                    action();
            };
            ee.PreUnsafeUpdate += (u, q) => action();
        }

        static GenericInvoker<Action<Schema, Action>> giAttachInvalidations = new GenericInvoker<Action<Schema, Action>>((s, a) => AttachInvalidations<Entity>(s, a));
        static void AttachInvalidations<T>(Schema s, Action action) where T : Entity
        {
            var ee = s.EntityEvents<T>();

            ee.Saving += e =>
            {
                if (e.IsGraphModified)
                    action();
            };
            ee.PreUnsafeUpdate += (u, eq) => action();
            ee.PreUnsafeDelete += (q) => action();
        }

        public virtual void OnLoad(SchemaBuilder sb, InvalidateWith invalidateWith)
        {
        }
    }


    internal class ViewBuilder : SchemaBuilder
    {
        public ViewBuilder(Schema schema)
            : base(schema)
        {
        }

        public override Table Include(Type type)
        {
            return Schema.Table(type);
        }

        public Table NewView(Type type)
        {
            Table table = new Table(type)
            {
                Name = GenerateTableName(type, Settings.TypeAttributes<TableNameAttribute>(type)),
                IsView = true
            };

            table.Fields = GenerateFields(PropertyRoute.Root(type), table, NameSequence.Void, forceNull: false, inMList: false);

            table.GenerateColumns();

            return table;
        }


        public override ObjectName GenerateTableName(Type type, TableNameAttribute tn)
        {
            if (tn != null)
            {
                if (tn.SchemaName == "sys")
                {
                    DatabaseName db = Administrator.sysViewDatabase.Value;

                    return new ObjectName(new SchemaName(db, tn.SchemaName ?? "dbo"), tn.Name);
                }
            }

            return base.GenerateTableName(type, tn);
        }


        protected override FieldReference GenerateFieldReference(ITable table, PropertyRoute route, NameSequence name, bool forceNull)
        {
            return base.GenerateFieldReference(table, route, name, forceNull);
        }

        protected override SchemaBuilder.KindOfField? GetKindOfField(PropertyRoute route)
        {
            if (route.FieldInfo != null && route.FieldInfo.GetCustomAttribute<ViewPrimaryKeyAttribute>() != null)
                return SchemaBuilder.KindOfField.PrimaryKey;

            return base.GetKindOfField(route);
        }

        protected override Field GenerateFieldPrimaryKey(Table table, PropertyRoute route, NameSequence name)
        {
            var att = Settings.FieldAttribute<SqlDbTypeAttribute>(route);

            SqlDbTypePair pair = Settings.GetSqlDbType(att, route.Type);

            var result = new FieldValue(route.Type)
            {
                PrimaryKey = true,
                Name = name.ToString(),
                SqlDbType = pair.SqlDbType,
                UserDefinedTypeName = pair.UserDefinedTypeName,
                Nullable = Settings.IsNullable(route, false),
                Size = Settings.GetSqlSize(att, pair.SqlDbType),
                Scale = Settings.GetSqlScale(att, pair.SqlDbType),
            };

            return result;
        }
    }
}

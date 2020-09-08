using Breeze.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Breeze.Persistence.EFCore {


  public class MetadataBuilder {

    public static BreezeMetadata BuildFrom(DbContext dbContext) {
      return new MetadataBuilder().GetMetadataFromContext(dbContext);
    }

    private BreezeMetadata GetMetadataFromContext(DbContext dbContext) {
      var metadata = new BreezeMetadata();
      var dbSetMap = GetDbSetMap(dbContext);
      metadata.StructuralTypes = dbContext.Model.GetEntityTypes()
        .Where(et => !et.IsOwned())
        .Select(et => CreateMetaType(et, dbSetMap)).ToList();

      var enums = dbContext.Model.GetEntityTypes().Where(et => et.GetProperties().Any(p => p.PropertyInfo != null && p.PropertyInfo.PropertyType.IsEnum)).Select(et => et.GetProperties().Where(p => p.PropertyInfo.PropertyType.IsEnum)).ToList();

      foreach (var myEnum in enums) {
        var realType = myEnum.First().ClrType;
        string[] enumNames = Enum.GetNames(realType);
        var p = new Dictionary<string, object>();
        p.Add("shortName", realType.Name);
        p.Add("namespace", realType.Namespace);
        p.Add("values", enumNames);
        if (!metadata.EnumTypes.Exists(x => x.ContainsValue(realType.Name))) {
          metadata.EnumTypes.Add(p);
        }
      }

      var complexTypes = dbContext.Model.GetEntityTypes()
        .Where(et => et.IsOwned())
        .Select(et => CreateMetaType(et, dbSetMap)).ToList();
      // Complex types show up once per parent reference and we need to reduce
      // this to just the unique types.
      var complexTypesMap = complexTypes.ToDictionary(mt => mt.ShortName);
      complexTypesMap.Values.ToList().ForEach(v => metadata.StructuralTypes.Insert(0, v));

      return metadata;
    }

    private static Dictionary<Type, String> GetDbSetMap(DbContext context) {
      var dbSetProperties = new List<PropertyInfo>();
      var properties = context.GetType().GetProperties();
      var result = new Dictionary<Type, String>();
      foreach (var property in properties) {
        var setType = property.PropertyType;
        var isDbSet = setType.IsGenericType && (typeof(DbSet<>).IsAssignableFrom(setType.GetGenericTypeDefinition()));
        if (isDbSet) {
          var entityType = setType.GetGenericArguments()[0];
          var resourceName = property.Name;
          result.Add(entityType, resourceName);
        }
      }
      return result;
    }

    private MetaType CreateMetaType(Microsoft.EntityFrameworkCore.Metadata.IEntityType et, Dictionary<Type, String> dbSetMap) {
      var mt = new MetaType {
        ShortName = et.ClrType.Name,
        Namespace = et.ClrType.Namespace
      };
      if (et.IsOwned()) {
        mt.IsComplexType = true;
      }
      if (dbSetMap.TryGetValue(et.ClrType, out string resourceName)) {
        mt.DefaultResourceName = resourceName;
      }
      var baseType = et.BaseType;
      if (baseType != null) {
        mt.BaseTypeName = baseType.ClrType.Name + ":#" + baseType.ClrType.Namespace;
      }

      // exclude base class properties 
      mt.DataProperties = et.GetProperties().Where(p => p.DeclaringType.ClrType.Equals(et.ClrType)).Select(p => CreateDataProperty(p)).ToList();


      // EF returns parent's key with the complex type - we need to remove this.
      if (mt.IsComplexType) {
        mt.DataProperties = mt.DataProperties.Where(dp => dp.IsPartOfKey == null).ToList();
      }

      if (!mt.IsComplexType) {
        mt.AutoGeneratedKeyType = mt.DataProperties.Any(dp => dp.IsIdentityColumn) ? AutoGeneratedKeyType.Identity : AutoGeneratedKeyType.None;
      }

      // Handle complex properties
      // for now this only complex types ( 'owned types' in EF parlance are eager loaded)
      var ownedNavigations = et.GetNavigations().Where(n => n.GetTargetType().IsOwned());
      ownedNavigations.ToList().ForEach(n => {
        var complexType = n.GetTargetType().ClrType;
        var dp = new MetaDataProperty();
        dp.NameOnServer = n.Name;
        dp.IsNullable = false;
        dp.IsPartOfKey = false;
        dp.ComplexTypeName = NormalizeTypeName(complexType);
        mt.DataProperties.Add(dp);
      });

      mt.NavigationProperties = et.GetNavigations()
        .Where(n => !n.GetTargetType().IsOwned() && n.DeclaringType.ClrType.Equals(et.ClrType)).Select(p => CreateNavProperty(p)).ToList();

      return mt;
    }

    private MetaDataProperty CreateDataProperty(IProperty p) {
      var dp = new MetaDataProperty();

      dp.NameOnServer = p.Name;
      dp.IsNullable = p.IsNullable;
      dp.IsPartOfKey = p.IsPrimaryKey() ? true : (bool?)null;
      dp.IsIdentityColumn = p.IsPrimaryKey() && p.ValueGenerated == ValueGenerated.OnAdd;
      dp.MaxLength = p.GetMaxLength();
      dp.DataType = NormalizeDataTypeName(p.ClrType);
      dp.ConcurrencyMode = p.IsConcurrencyToken ? "Fixed" : null;
      var dfa = p.GetAnnotations().Where(a => a.Name == "DefaultValue").FirstOrDefault();
      if (dfa != null) {
        dp.DefaultValue = dfa.Value;
      } else if (!p.IsNullable) {
        // TODO: this should really be done on the client.
        if (p.ClrType == typeof(TimeSpan)) {
          dp.DefaultValue = "PT0S";
        }
        // dp.DefaultValue = // get default value for p.ClrType datatype
      }
      dp.AddValidators(p.ClrType);
      return dp;
    }

    private MetaNavProperty CreateNavProperty(INavigation p) {
      var np = new MetaNavProperty();
      np.NameOnServer = p.Name;
      np.EntityTypeName = NormalizeTypeName(p.GetTargetType().ClrType);
      np.IsScalar = !p.IsCollection();
      // FK_<dependent type name>_<principal type name>_<foreign key property name>
      np.AssociationName = BuildAssocName(p);
      if (p.IsDependentToPrincipal()) {
        np.AssociationName = BuildAssocName(p);
        np.ForeignKeyNamesOnServer = p.ForeignKey.Properties.Select(fkp => fkp.Name).ToList();
      } else {
        var invP = p.FindInverse();
        string assocName;
        if (invP == null) {
          assocName = "Inv_" + BuildAssocName(p);
        } else {
          assocName = BuildAssocName(invP);
        }
        np.AssociationName = assocName;
        np.InvForeignKeyNamesOnServer = p.ForeignKey.Properties.Select(fkp => fkp.Name).ToList();
      }

      return np;
    }

    private string BuildAssocName(INavigation prop) {
      var assocName = prop.DeclaringEntityType.Name + "_" + prop.GetTargetType().Name + "_" + prop.Name;
      return assocName;
    }

    private string NormalizeTypeName(Type type) {
      return type.Name + ":#" + type.Namespace;
    }

    private string NormalizeDataTypeName(Type type) {
      type = TypeFns.GetNonNullableType(type);
      var result = type.ToString().Replace("System.", "");
      if (result == "Byte[]") {
        return "Binary";
      } else {
        return result;
      }
    }

  }   


}

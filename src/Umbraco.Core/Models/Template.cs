﻿using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Umbraco.Core.Configuration;
using Umbraco.Core.IO;
using Umbraco.Core.Models.EntityBase;
using Umbraco.Core.Services;
using Umbraco.Core.Strings;

namespace Umbraco.Core.Models
{
    /// <summary>
    /// Represents a Template file
    /// </summary>
    [Serializable]
    [DataContract(IsReference = true)]
    public class Template : File, ITemplate
    {
        private readonly IFileSystem _viewFileSystem;
        private string _alias;
        private string _name;
        private string _masterTemplateAlias;
        private Lazy<int> _masterTemplateId;

        private static readonly PropertyInfo MasterTemplateAliasSelector = ExpressionHelper.GetPropertyInfo<Template, string>(x => x.MasterTemplateAlias);
        private static readonly PropertyInfo MasterTemplateIdSelector = ExpressionHelper.GetPropertyInfo<Template, Lazy<int>>(x => x.MasterTemplateId);

        public Template(string name, string alias)
            : base(string.Empty)
        {
            _name = name;
            _alias = alias.ToCleanString(CleanStringType.UnderscoreAlias);
            _masterTemplateId = new Lazy<int>(() => -1);
            _viewFileSystem = new PhysicalFileSystem(SystemDirectories.MvcViews);
        }

        public Template(string name, string alias, IFileSystem viewFileSystem)
            : this(name, alias)
        {
            if (viewFileSystem == null) throw new ArgumentNullException("viewFileSystem");
            _viewFileSystem = viewFileSystem;
        }

        [Obsolete("This constructor should not be used, file path is determined by alias, setting the path here will have no affect")]
        public Template(string path, string name, string alias)
            : base(path)
        {
            _name = name;
            _alias = alias.ToCleanString(CleanStringType.UnderscoreAlias);
            _masterTemplateId = new Lazy<int>(() => -1);
        }

        [DataMember]
        public Lazy<int> MasterTemplateId
        {
            get { return _masterTemplateId; }
            set
            {
                SetPropertyValueAndDetectChanges(o =>
                {
                    _masterTemplateId = value;
                    return _masterTemplateId;
                }, _masterTemplateId, MasterTemplateIdSelector);
            }
        }

        public string MasterTemplateAlias
        {
            get { return _masterTemplateAlias; }
            set
            {
                SetPropertyValueAndDetectChanges(o =>
                {
                    _masterTemplateAlias = value;
                    return _masterTemplateAlias;
                }, _masterTemplateAlias, MasterTemplateAliasSelector);
            }
        }

        [DataMember]
        string ITemplate.Name
        {
            get { return _name; }
            set { _name = value; }
        }

        [DataMember]
        string ITemplate.Alias
        {
            get { return _alias; }
            set { _alias = value; }
        }

        public override string Alias
        {
            get { return ((ITemplate)this).Alias; }
        }
        
        public override string Name
        {
            get { return ((ITemplate)this).Name; }
        }


        /// <summary>
        /// Returns true if the template is used as a layout for other templates (i.e. it has 'children')
        /// </summary>
        public bool IsMasterTemplate { get; internal set; }

        /// <summary>
        /// Returns the <see cref="RenderingEngine"/> that corresponds to the template file
        /// </summary>
        /// <returns><see cref="RenderingEngine"/></returns>
        public RenderingEngine GetTypeOfRenderingEngine()
        {
            return DetermineRenderingEngine();
        }

        /// <summary>
        /// Boolean indicating whether the file could be validated
        /// </summary>
        /// <returns>True if file is valid, otherwise false</returns>
        public override bool IsValid()
        {
            var exts = new List<string>();
            if (UmbracoConfig.For.UmbracoSettings().Templates.DefaultRenderingEngine == RenderingEngine.Mvc)
            {
                exts.Add("cshtml");
                exts.Add("vbhtml");
            }
            else
            {
                exts.Add(UmbracoConfig.For.UmbracoSettings().Templates.UseAspNetMasterPages ? "master" : "aspx");
            }

            var dirs = SystemDirectories.Masterpages;
            if (UmbracoConfig.For.UmbracoSettings().Templates.DefaultRenderingEngine == RenderingEngine.Mvc)
                dirs += "," + SystemDirectories.MvcViews;

            //Validate file
            var validFile = IOHelper.VerifyEditPath(Path, dirs.Split(','));

            //Validate extension
            var validExtension = IOHelper.VerifyFileExtension(Path, exts);

            return validFile && validExtension;
        }

        /// <summary>
        /// Method to call when Entity is being saved
        /// </summary>
        /// <remarks>Created date is set and a Unique key is assigned</remarks>
        internal override void AddingEntity()
        {
            base.AddingEntity();

            if (Key == Guid.Empty)
                Key = Guid.NewGuid();
        }


        public void SetMasterTemplate(ITemplate masterTemplate)
        {
            MasterTemplateId = new Lazy<int>(() => masterTemplate.Id);
        }

        public override object DeepClone()
        {
            var clone = (Template)base.DeepClone();

            //need to manually assign since they are readonly properties
            clone._alias = Alias;
            clone._name = Name;

            clone.ResetDirtyProperties(false);

            return clone;
        }

        /// <summary>
        /// This checks what the default rendering engine is set in config but then also ensures that there isn't already 
        /// a template that exists in the opposite rendering engine's template folder, then returns the appropriate 
        /// rendering engine to use.
        /// </summary> 
        /// <returns></returns>
        /// <remarks>
        /// The reason this is required is because for example, if you have a master page file already existing under ~/masterpages/Blah.aspx
        /// and then you go to create a template in the tree called Blah and the default rendering engine is MVC, it will create a Blah.cshtml 
        /// empty template in ~/Views. This means every page that is using Blah will go to MVC and render an empty page. 
        /// This is mostly related to installing packages since packages install file templates to the file system and then create the 
        /// templates in business logic. Without this, it could cause the wrong rendering engine to be used for a package.
        /// </remarks>
        private RenderingEngine DetermineRenderingEngine()
        {
            var engine = UmbracoConfig.For.UmbracoSettings().Templates.DefaultRenderingEngine;

            if (Content.IsNullOrWhiteSpace() == false && MasterPageHelper.IsMasterPageSyntax(Content))
            {
                //there is a design but its definitely a webforms design
                return RenderingEngine.WebForms;
            }

            var viewHelper = new ViewHelper(_viewFileSystem);

            switch (engine)
            {
                case RenderingEngine.Mvc:
                    //check if there's a view in ~/masterpages
                    if (MasterPageHelper.MasterPageExists(this) && viewHelper.ViewExists(this) == false)
                    {
                        //change this to webforms since there's already a file there for this template alias
                        engine = RenderingEngine.WebForms;
                    }
                    break;
                case RenderingEngine.WebForms:
                    //check if there's a view in ~/views
                    if (viewHelper.ViewExists(this) && MasterPageHelper.MasterPageExists(this) == false)
                    {
                        //change this to mvc since there's already a file there for this template alias
                        engine = RenderingEngine.Mvc;
                    }
                    break;
            }
            return engine;
        }
        
    }
}

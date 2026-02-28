using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Diffusion.Database.Models;
using Diffusion.Toolkit.Classes;
using Diffusion.Toolkit.Models;
using Diffusion.Toolkit.Services;

namespace Diffusion.Toolkit
{
    public partial class SmartAlbumEditorWindow : BorderlessWindow
    {
        private readonly SmartAlbumEditorModel _model;

        public SmartAlbumEditorWindow()
        {
            _model = new SmartAlbumEditorModel();
            _model.Escape = new RelayCommand<object>(o => Close());

            InitializeComponent();
            DataContext = _model;

            RefreshSmartAlbums();
        }

        private void RefreshSmartAlbums()
        {
            var albums = ServiceLocator.DataStore!.GetAllSmartAlbums();
            _model.SmartAlbums = new ObservableCollection<SmartAlbum>(albums);
        }

        private void SmartAlbumList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_model.SelectedSmartAlbum == null) return;

            var sa = _model.SelectedSmartAlbum;
            _model.Name = sa.Name;
            _model.MatchAll = sa.MatchAll;

            var rules = SmartAlbumEvaluator.DeserializeRules(sa.RulesJson);
            var ruleViewModels = new ObservableCollection<SmartAlbumRuleViewModel>();

            foreach (var rule in rules)
            {
                var vm = new SmartAlbumRuleViewModel
                {
                    Field = rule.Field,
                    Operator = rule.Operator,
                    Value = rule.Value,
                    AvailableOperators = new ObservableCollection<SmartAlbumOperator>(
                        SmartAlbumRule.GetOperatorsForField(rule.Field))
                };
                ruleViewModels.Add(vm);
            }

            _model.Rules = ruleViewModels;
        }

        private void NewSmartAlbum_OnClick(object sender, RoutedEventArgs e)
        {
            _model.SelectedSmartAlbum = null;
            _model.Name = "New Smart Album";
            _model.MatchAll = true;
            _model.Rules = new ObservableCollection<SmartAlbumRuleViewModel>();
            _model.PreviewCount = 0;
        }

        private void DeleteSmartAlbum_OnClick(object sender, RoutedEventArgs e)
        {
            if (_model.SelectedSmartAlbum == null) return;

            var result = MessageBox.Show(
                $"Delete smart album '{_model.SelectedSmartAlbum.Name}'?",
                "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                ServiceLocator.DataStore!.DeleteSmartAlbum(_model.SelectedSmartAlbum.Id);
                RefreshSmartAlbums();
                _model.SelectedSmartAlbum = null;
                _model.Name = "";
                _model.Rules.Clear();
            }
        }

        private void AddRule_OnClick(object sender, RoutedEventArgs e)
        {
            var rule = new SmartAlbumRuleViewModel
            {
                Field = SmartAlbumField.Rating,
                Operator = SmartAlbumOperator.GreaterThanOrEqual,
                Value = "7",
                AvailableOperators = new ObservableCollection<SmartAlbumOperator>(
                    SmartAlbumRule.GetOperatorsForField(SmartAlbumField.Rating))
            };
            _model.Rules.Add(rule);
        }

        private void RemoveRule_OnClick(object sender, RoutedEventArgs e)
        {
            if (_model.Rules.Count > 0)
            {
                _model.Rules.RemoveAt(_model.Rules.Count - 1);
            }
        }

        private void Field_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb && cb.DataContext is SmartAlbumRuleViewModel rule)
            {
                var operators = SmartAlbumRule.GetOperatorsForField(rule.Field);
                rule.AvailableOperators = new ObservableCollection<SmartAlbumOperator>(operators);
                if (!operators.Contains(rule.Operator) && operators.Count > 0)
                {
                    rule.Operator = operators[0];
                }
            }
        }

        private void Preview_OnClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var rules = GetCurrentRules();
                var (whereClause, parameters) = SmartAlbumEvaluator.BuildQuery(rules, _model.MatchAll);
                var count = ServiceLocator.DataStore!.CountSmartAlbumResults(whereClause, parameters);
                _model.PreviewCount = count;
                _model.Status = $"Preview: {count} images match the current rules";
            }
            catch (Exception ex)
            {
                _model.Status = $"Error: {ex.Message}";
            }
        }

        private void Save_OnClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_model.Name))
            {
                MessageBox.Show("Please enter a name for the smart album.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var rules = GetCurrentRules();
            var rulesJson = SmartAlbumEvaluator.SerializeRules(rules);

            if (_model.SelectedSmartAlbum != null)
            {
                var sa = _model.SelectedSmartAlbum;
                sa.Name = _model.Name;
                sa.MatchAll = _model.MatchAll;
                sa.RulesJson = rulesJson;
                ServiceLocator.DataStore!.UpdateSmartAlbum(sa);
            }
            else
            {
                var sa = new SmartAlbum
                {
                    Name = _model.Name,
                    MatchAll = _model.MatchAll,
                    RulesJson = rulesJson
                };
                ServiceLocator.DataStore!.CreateSmartAlbum(sa);
            }

            RefreshSmartAlbums();
            _model.Status = $"Smart album '{_model.Name}' saved";
        }

        private List<SmartAlbumRule> GetCurrentRules()
        {
            return _model.Rules.Select(r => new SmartAlbumRule
            {
                Field = r.Field,
                Operator = r.Operator,
                Value = r.Value
            }).ToList();
        }
    }
}

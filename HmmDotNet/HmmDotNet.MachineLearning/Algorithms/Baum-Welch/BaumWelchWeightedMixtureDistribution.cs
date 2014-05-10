﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using HmmDotNet.MachineLearning.Algorithms.VaribaleEstimation.EstimationParameters;
using HmmDotNet.MachineLearning.Base;
using HmmDotNet.MachineLearning.HiddenMarkovModels;
using HmmDotNet.Mathematic.Extentions;
using HmmDotNet.Statistics.Distributions;
using HmmDotNet.Statistics.Distributions.Multivariate;

namespace HmmDotNet.MachineLearning.Algorithms.Baum_Welch
{
    public class BaumWelchWeightedMixtureDistribution :  BaseBaumWelch<Mixture<IMultivariateDistribution>>, IBaumWelchAlgorithm<Mixture<IMultivariateDistribution>>
    {
        #region Private Members

        private GammaEstimator<Mixture<IMultivariateDistribution>> _gammaEstimator;
        private KsiEstimator<Mixture<IMultivariateDistribution>> _ksiEstimator;

        private readonly IList<IObservation> _observations;
        private readonly decimal[] _observationWeights;
        
        private IHiddenMarkovModel<Mixture<IMultivariateDistribution>> _estimatedModel;
        private IHiddenMarkovModel<Mixture<IMultivariateDistribution>> _currentModel;

        private Mixture<IMultivariateDistribution>[] _estimatedEmissions;

        #endregion Private Members

        public BaumWelchWeightedMixtureDistribution(IList<IObservation> observations, decimal[] observationWeights, IHiddenMarkovModel<Mixture<IMultivariateDistribution>> model)
            : base(model)
        {
            _observations = observations;
            _observationWeights = observationWeights;

            _currentModel = model;
            _estimatedEmissions = new Mixture<IMultivariateDistribution>[_currentModel.N];
            for (var i = 0; i < model.N; i++)
            {
                // BUG : Update emmisions from model. Don't create new ones.
                _estimatedEmissions[i] = new Mixture<IMultivariateDistribution>(model.Emission[0].Components.Length, model.Emission[0].Dimension);
            }

            _estimatedModel = HiddenMarkovModelStateFactory.GetState(new ModelCreationParameters<Mixture<IMultivariateDistribution>> { Pi = _estimatedPi, TransitionProbabilityMatrix = _estimatedTransitionProbabilityMatrix, Emissions = _estimatedEmissions });//new HiddenMarkovModelState<Mixture<IMultivariateDistribution>>(_estimatedPi, _estimatedTransitionProbabilityMatrix, _estimatedEmissions);

            Normalized = _estimatedModel.Normalized = model.Normalized;
        }

        public bool Normalized { get; set; }

        public IHiddenMarkovModel<Mixture<IMultivariateDistribution>> Run(int maxIterations, double likelihoodTolerance)
        {
            // Initialize responce object            
            var forwardBackward = new ForwardBackward(Normalized);
            do
            {
                maxIterations--;
                if (!_estimatedModel.Likelihood.EqualsTo(0))
                {
                    _currentModel = HiddenMarkovModelStateFactory.GetState(new ModelCreationParameters<Mixture<IMultivariateDistribution>> { Pi = _estimatedPi, TransitionProbabilityMatrix = _estimatedTransitionProbabilityMatrix, Emissions = _estimatedEmissions });//new HiddenMarkovModelState<Mixture<IMultivariateDistribution>>(_estimatedPi, _estimatedTransitionProbabilityMatrix, _estimatedEmissions) { LogNormalized = _estimatedModel.LogNormalized };
                    _currentModel.Normalized = Normalized;
                    _currentModel.Likelihood = _estimatedModel.Likelihood;
                }
                // Run Forward-Backward procedure
                forwardBackward.RunForward(_observations, _currentModel);
                forwardBackward.RunBackward(_observations, _currentModel);
                // Calculate Gamma and Xi                 
                var @params = new MixtureSigmaEstimationParameters<Mixture<IMultivariateDistribution>>
                {
                    Alpha = forwardBackward.Alpha,
                    Beta = forwardBackward.Beta,
                    Observations = _observations,
                    Model = _currentModel,
                    Normalized = _currentModel.Normalized,
                    L = _currentModel.Emission[0].Components.Length,
                    ObservationWeights = _observationWeights
                };
                _gammaEstimator = new GammaEstimator<Mixture<IMultivariateDistribution>>();
                _ksiEstimator = new KsiEstimator<Mixture<IMultivariateDistribution>>();
                var mixtureCoefficientsEstimator = new MixtureCoefficientsEstimator<Mixture<IMultivariateDistribution>>();
                var mixtureMuEstimator = new MixtureMuEstimator<Mixture<IMultivariateDistribution>>(); // Mean
                var mixtureSigmaEstimator = new MixtureSigmaEstimator<Mixture<IMultivariateDistribution>>(); // Covariance
                var mixtureGammaEstimator = new MixtureGammaEstimator<Mixture<IMultivariateDistribution>>();
                @params.Gamma = _gammaEstimator.Estimate(@params);
                @params.GammaComponents = mixtureGammaEstimator.Estimate(@params);


                EstimatePi(_gammaEstimator.Estimate(@params));
                // TODO : weights for A
                EstimateTransitionProbabilityMatrix(_gammaEstimator.Estimate(@params), _ksiEstimator.Estimate(@params), _observationWeights, _observations.Count);

                for (var n = 0; n < _currentModel.N; n++)
                {
                    var mixturesComponents = _currentModel.Emission[n].Coefficients.Length;
                    var distributions = new IMultivariateDistribution[mixturesComponents];
                    // Calculate coefficients for state n
                    // TODO : weights for W
                    var coefficients = mixtureCoefficientsEstimator.Estimate(@params)[n];
                    if (Normalized)
                    {
                        mixtureCoefficientsEstimator.Denormalize();
                    }
                    // TODO : weights Mu
                    @params.Mu = mixtureMuEstimator.Estimate(@params);
                    for (var l = 0; l < mixturesComponents; l++)
                    {
                        // TODO : weights Sigma
                        distributions[l] = new NormalDistribution(mixtureMuEstimator.Estimate(@params)[n, l], mixtureSigmaEstimator.Estimate(@params)[n, l]);
                    }
                    _estimatedEmissions[n] = new Mixture<IMultivariateDistribution>(coefficients, distributions);
                }
                _estimatedModel = HiddenMarkovModelStateFactory.GetState(new ModelCreationParameters<Mixture<IMultivariateDistribution>> { Pi = _estimatedPi, TransitionProbabilityMatrix = _estimatedTransitionProbabilityMatrix, Emissions = _estimatedEmissions });
                _estimatedModel.Normalized = Normalized;
                _estimatedModel.Likelihood = forwardBackward.RunForward(_observations, _estimatedModel);
                _likelihoodDelta = Math.Abs(Math.Abs(_currentModel.Likelihood) - Math.Abs(_estimatedModel.Likelihood));
                Debug.WriteLine("Iteration {3} , Current {0}, Estimate {1} Likelihood delta {2}", _currentModel.Likelihood, _estimatedModel.Likelihood, _likelihoodDelta, maxIterations);
            }
            while (_currentModel != _estimatedModel && maxIterations > 0 && _likelihoodDelta > likelihoodTolerance);

            return _estimatedModel;
        }
    }
}

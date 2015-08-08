﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Fusion;
using Fusion.Graphics;
using Fusion.Mathematics;
using Fusion.Input;

namespace GraphVis
{
	class CalculationSystemAuto : CalculationSystem
	{
		float	stepLength;

		float	energy;
		float	deltaEnergy;
		float	pGradE;
		int		stepStability;
		float	checkSum;


		public CalculationSystemAuto(LayoutSystem host)
			: base(host)
		{
			stepLength		= 0.01f;
			stepStability	= 0;
			checkSum		= 0;
		}


		public bool FixedStep
		{
			get;
			set;
		}

		/// <summary>
		/// This function performs the first iteration of calculations
		/// </summary>
		protected override void initCalculations()
		{
			LayoutSystem.ComputeParams param = new LayoutSystem.ComputeParams();
			param.StepLength = stepLength;
			HostSystem.CalcDescentVector(HostSystem.CurrentStateBuffer, param); // calc desc vector and energies
			HostSystem.CalcTotalEnergyAndDotProduct(HostSystem.CurrentStateBuffer, HostSystem.CurrentStateBuffer,
					HostSystem.EnergyBuffer, param, out energy, out pGradE, out checkSum);
		}


		/// <summary>
		/// This function resets all the values to initial state
		/// </summary>
		protected override void resetState()
		{
			stepLength = 0.01f;
			numIterations = 0;
			stepStability = 0;
			FixedStep = false;
		}


		/// <summary>
		/// This is the main function to call at each iteration 
		/// </summary>
		/// <param name="userCommand"></param>
		protected override void update(int userCommand)
		{
			var graphSys = HostSystem.Environment.GetService<GraphSystem>();
			LayoutSystem.ComputeParams param = new LayoutSystem.ComputeParams();

			bool cond1 = false;
			bool cond2 = false;
			float energyThreshold = (float)HostSystem.ParticleCount / 10000.0f;
			float chosenStepLength = stepLength;

			// Wolfe constants:
			//		float C1 = 0.1f;
			//		float C2 = 0.99f;

			float C1 = 0.3f;
			float C2 = 0.99f;

			// Algorithm outline:
			//
			//  1. Calc descent vector pk
			//  2. calc pk * grad(Ek)
			//  3. calc Ek
			//  4. try move with some step factor
			//  5. calc pk * grad(Ek+1)
			//  6. calc Ek+1
			//  7. check Wolfe conditions
			//  8. if both are OK GOTO 11
			//  9. modify step factor
			// 10. GOTO 4
			// 11. swap try buffer with current buffer
			// 12. GOTO 1
			// 




			if (HostSystem.CurrentStateBuffer != null)
			{
				// manual step change:
				if (userCommand > 0)
				{
					stepLength = increaseStep(stepLength);
				}
				if (userCommand < 0)
				{
					stepLength = decreaseStep(stepLength);
				}


				if (HostSystem.RunPause == LayoutSystem.State.RUN)
				{
					//			StreamWriter sw = File.AppendText( "step.csv" );
					for (int i = 0; i < graphSys.Config.IterationsPerFrame; ++i)
					{
						float Ek = energy;		// current energy
						float Ek1 = 0;			// next energy

						float pkGradEk = 0;		// current dot prodct
						float pkGradEk1 = 0;	// next dot prodct

						cond1 = false;
						cond2 = false;

						int tries = 0;

						param.StepLength = stepLength;
						if (!FixedStep)
						{
							HostSystem.CalcTotalEnergyAndDotProduct(
								HostSystem.CurrentStateBuffer,
								HostSystem.CurrentStateBuffer,
								HostSystem.EnergyBuffer,
								param, out Ek, out pkGradEk, out checkSum);
						}

						while (!(cond1 && cond2))
						{
							if (FixedStep)
							{
								cond1 = true;
								cond2 = true;
							}

							param.StepLength = stepLength;

							HostSystem.MoveVertices(
								HostSystem.CurrentStateBuffer,
								HostSystem.NextStateBuffer,
								param);	// move vertices in descent direction

							HostSystem.CalcDescentVector(
								HostSystem.NextStateBuffer,
								param);	// calculate energies and next descent vectors

							if (!FixedStep)
							{
								HostSystem.CalcTotalEnergyAndDotProduct(
									HostSystem.CurrentStateBuffer,
									HostSystem.NextStateBuffer,
									HostSystem.EnergyBuffer,
									param, out Ek1, out pkGradEk1, out checkSum);


								// check Wolfe conditions:
								cond1 = (Ek1 - Ek <= stepLength * C1 * pkGradEk);
								cond2 = (pkGradEk1 >= C2 * pkGradEk);

								//// if we are very close to minimum, do not check conditions (it leads to infinite cycles)
								//if (Math.Abs(Ek1 - Ek) < energyThreshold)
								//{
								//	cond1 = cond2 = true;
								//}

								//// Debug output:
								//if (tries > 4)
								//{
								//	Console.WriteLine("step = " + stepLength + " " +
								//		"cond#1 = " + (cond1 ? "TRUE" : "FALSE") + " " +
								//		"cond#2 = " + (cond2 ? "TRUE" : "FALSE") + " " +
								//		"deltaE = " + (Ek1 - Ek)
								//		);
								//}

								// change step length:
								if (cond1 && !cond2) { stepLength = increaseStep(stepLength); }
								if (!cond1 && !cond2) { stepLength = increaseStep(stepLength); }

								if (!cond1 && cond2) { stepLength = decreaseStep(stepLength); }

							}
							++tries;
							++numIterations;

							// To prevent freeze:
							if (tries >= graphSys.Config.SearchIterations) break;
						}
						// swap buffers: --------------------------------------------------------------------
						HostSystem.SwapBuffers();

						if (stepLength == chosenStepLength) // if the new step length is the same as before
						{
							++stepStability;
						}
						else
						{
							stepStability = 0;
						}
						chosenStepLength = stepLength;

						if (stepStability >= graphSys.Config.SwitchToManualAfter) // if stable step length found, switch to fixed step
						{
							FixedStep = true;
						}

						energy = Ek1;
						deltaEnergy = Ek1 - Ek;
						pGradE = pkGradEk1;
					}
				}
			}

			var debStr = HostSystem.Environment.GetService<DebugStrings>();

			debStr.Add(Color.Black,		"AUTO MODE");
			debStr.Add(Color.Aqua,		"Step factor  = " + chosenStepLength);
			debStr.Add(Color.Aqua,		"Energy         = " + energy);
//			debStr.Add(Color.Aqua,		"DeltaE         = " + deltaEnergy);
//			debStr.Add(Color.Aqua,		"pTp            = " + pGradE);
			debStr.Add(Color.Aqua,		"Iteration      = " + numIterations);
			debStr.Add(Color.RoyalBlue, "Mode:   " + (FixedStep ? "FIXED" : "AUTO"));
			debStr.Add(Color.Aqua,		"Stability       = " + stepStability);
			debStr.Add(Color.Orchid,	"Check sum     = " + checkSum);
		}



		/// <summary>
		/// This function returns increased step length
		/// </summary>
		/// <param name="step"></param>
		/// <returns></returns>
		float increaseStep(float step)
		{
			return step + 0.01f;
		}


		/// <summary>
		/// This function returns decreased step length
		/// </summary>
		/// <param name="step"></param>
		/// <returns></returns>
		float decreaseStep(float step)
		{
			return step - 0.01f;
		}

	}
}

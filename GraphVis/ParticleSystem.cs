﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Fusion;
using Fusion.Graphics;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace GraphVis {

	public enum IntegratorType
	{
		EULER			= 0x8,
		RUNGE_KUTTA		= 0x8 << 1
	}

	public class ParticleConfig
	{
		
		float maxParticleMass;
		float minParticleMass;
		float rotation;
		IntegratorType iType;

		[Category("Particle mass")]
		[Description("Largest particle mass")]
		public float Max_mass { get{ return  maxParticleMass; } set{ maxParticleMass = value; } }

		[Category("Particle mass")]
		[Description("Smallest particle mass")]
		public float Min_mass { get{ return  minParticleMass; } set{ minParticleMass = value; } }

		[Category("Integrator type")]
		[Description("Integrator type")]
		public IntegratorType IType{ get{ return iType; } set{ iType = value; } }

		[Category("Initial rotation")]
		[Description("Rate of initial rotation")]
		public float Rotation { get{ return  rotation; } set{ rotation = value; } }

		public ParticleConfig()
		{
			minParticleMass	= 0.5f;
			maxParticleMass	= 0.5f;
			rotation		= 2.6f;
			iType			= IntegratorType.RUNGE_KUTTA; 
		}
	}



	public class ParticleSystem : GameService {


		[Config]
		public ParticleConfig cfg{ get; set; }

		Texture2D	texture;
		Ubershader	shader;

		State		state;

		const int	BlockSize				=	512;

		const int	MaxInjectingParticles	=	5;
		const int	MaxSimulatedParticles	=	MaxInjectingParticles;

		float		MaxParticleMass;
		float		MinParticleMass;
		float		spinRate;
		float		linkSize;

		int					injectionCount = 0;
		Particle3d[]		injectionBufferCPU; // = new Particle3d[MaxInjectingParticles];
		StructuredBuffer	injectionBuffer;
		StructuredBuffer	simulationBufferSrc;

		StructuredBuffer	simulationBufferDst;
		StructuredBuffer	linksPtrBuffer;
		int[]				linksPtrBufferCPU; //		= new int[MaxInjectingParticles];




		int					linkCount;
		int					maxLinkCount;
		StructuredBuffer	linksBuffer;
		Link[]				linksBufferCPU;
		ConstantBuffer		paramsCB;
		List<int>[]			linkPtrLists;

		List<Link>			linkList;
		List<Particle3d>	ParticleList;


		// Particle in 3d space:
		[StructLayout(LayoutKind.Explicit)]
			struct Particle3d {
			[FieldOffset( 0)] public Vector4	Position;
			[FieldOffset(16)] public Vector3	Velocity;	
			[FieldOffset(28)] public Vector4	Color0;
			[FieldOffset(44)] public float		Size0;
			[FieldOffset(48)] public float		TotalLifeTime;
			[FieldOffset(52)] public float		LifeTime;
			[FieldOffset(56)] public int		linksPtr;
			[FieldOffset(60)] public int		linksCount;
			[FieldOffset(64)] public Vector3	Acceleration;


			public override string ToString ()
			{
				return string.Format("life time = {0}/{1}", LifeTime, TotalLifeTime );
			}

		}


		// link between 2 particles:
		[StructLayout(LayoutKind.Explicit)]
		struct Link
		{
			[FieldOffset( 0)] public int par1;
			[FieldOffset( 4)] public int par2;
			[FieldOffset( 8)] public float force1;
			[FieldOffset(12)] public float force2;
			[FieldOffset(16)] public Vector3 orientation;
		}

		enum Flags {
			// for compute shader:
			INJECTION		=	0x1,
			SIMULATION		=	0x1 << 1,
			MOVE			=	0x1 << 2,
			EULER			=	0x1 << 3,
			RUNGE_KUTTA		=	0x1 << 4,
			// for geometry shader:
			POINT			=	0x1 << 5,
			LINE			=	0x1 << 6
		}

		enum State {
			RUN,
			PAUSE
		}

		[StructLayout(LayoutKind.Explicit)]
		struct Params {
			[FieldOffset(  0)] public Matrix	View;
			[FieldOffset( 64)] public Matrix	Projection;
			[FieldOffset(128)] public int		MaxParticles;
			[FieldOffset(132)] public float		DeltaTime;
			[FieldOffset(136)] public float		LinkSize;
		} 

		Random rand = new Random();


		/// <summary>
		/// 
		/// </summary>
		/// <param name="game"></param>
		public ParticleSystem ( Game game ) : base (game)
		{
			cfg = new ParticleConfig();
		}


		/// <summary>
		/// 
		/// </summary>
		public override void Initialize ()
		{
			texture		=	Game.Content.Load<Texture2D>("particle");
			shader		=	Game.Content.Load<Ubershader>("shaders");
			shader.Map( typeof(Flags) );

			maxLinkCount		=	MaxSimulatedParticles * MaxSimulatedParticles;

			paramsCB			=	new ConstantBuffer( Game.GraphicsDevice, typeof(Params) );

			injectionBuffer		=	new StructuredBuffer( Game.GraphicsDevice, typeof(Particle3d), MaxInjectingParticles, StructuredBufferFlags.Counter );
			simulationBufferSrc	=	new StructuredBuffer( Game.GraphicsDevice, typeof(Particle3d), MaxSimulatedParticles, StructuredBufferFlags.Counter );
			simulationBufferDst	=	new StructuredBuffer( Game.GraphicsDevice, typeof(Particle3d), MaxSimulatedParticles, StructuredBufferFlags.Append );
			linksBuffer			=	new StructuredBuffer( Game.GraphicsDevice, typeof(Link),       maxLinkCount, StructuredBufferFlags.Counter );
			linksPtrBuffer		=	new StructuredBuffer( Game.GraphicsDevice, typeof(int),        maxLinkCount, StructuredBufferFlags.Counter );
			linkPtrLists		=	new List<int>[MaxSimulatedParticles];

			MaxParticleMass		=	cfg.Max_mass;
			MinParticleMass		=	cfg.Min_mass;
			spinRate			=	cfg.Rotation;
			linkSize			=	10.0f;

			linkCount			=	0;

	//		linksBufferCPU		=	new Link[maxLinkCount];
	//		linksPtrBufferCPU	=	new int[maxLinkCount];
			linkList			=	new List<Link>();
			ParticleList		=	new List<Particle3d>();

			state				=	State.RUN;

			base.Initialize();
		}



		public void Pause()
		{
			if ( state == State.RUN ) {
				state = State.PAUSE;
			}
			else {
				state = State.RUN;
			}
		}



		/// <summary>
		/// Returns random radial vector
		/// </summary>
		/// <returns></returns>
		Vector3 RadialRandomVector ()
		{
			Vector3 r;
			do {
				r	=	rand.NextVector3( -Vector3.One, Vector3.One );
			} while ( r.Length() > 1 );

			r.Normalize();

			return r;
		}




		public void AddMaxParticles( int N = MaxInjectingParticles )
		{
	
			addChain(N);
			setBuffers();

		}


		void addParticle( Vector3 pos, float lifeTime, float size0, float colorBoost = 1 )
		{
			float ParticleMass	=	rand.NextFloat( MinParticleMass, MaxParticleMass );
			ParticleList.Add( new Particle3d {
					Position = new Vector4( pos, ParticleMass ),
					Velocity		=	Vector3.Zero,
					Color0			=	rand.NextVector4( Vector4.Zero, Vector4.One ) * colorBoost,
					Size0			=	size0,
					TotalLifeTime	=	lifeTime,
					LifeTime		=	0,
					Acceleration	=	Vector3.Zero
				}
				
			);


		}


		void addLink( int end1, int end2 )
		{
			int linkNumber = linkList.Count;
			linkList.Add( new Link{
					par1 = end1,
					par2 = end2,
					force1 = 0,
					force2 = 0,
					orientation = Vector3.Zero
				}
			);
			if ( linkPtrLists[end1] == null ) {
				linkPtrLists[end1] = new List<int>();
			}
			linkPtrLists[end1].Add(linkNumber);

			if ( linkPtrLists[end2] == null ) {
				linkPtrLists[end2] = new List<int>();
			}
			linkPtrLists[end2].Add(linkNumber);

		}



		void addChain( int N )
		{
			linkPtrLists = new List<int>[N];
			Vector3 pos = Vector3.Zero;
			ParticleList.Clear();
			linkList.Clear();
			for ( int i = 0; i < N; ++i ) {
				
				addParticle( pos, 9999, 5.0f, 1.0f );
				pos += RadialRandomVector() * linkSize;
			}

			for ( int i = 1; i < N; ++i ) {
				addLink(i - 1, i);
			}
		}


		void setBuffers()
		{
			injectionBufferCPU = new Particle3d[ParticleList.Count];
			int iter = 0;
			foreach( var p in ParticleList ) {
				injectionBufferCPU[iter] = p;
				++iter;
			}
			linksBufferCPU = new Link[linkList.Count];
			iter = 0;
			foreach ( var l in linkList ) {
				linksBufferCPU[iter] = l;
				++iter;
			}

			linksPtrBufferCPU = new int[linkList.Count * 2];
			iter = 0;
			foreach( var ptrList in linkPtrLists ) {
				injectionBufferCPU[iter].linksPtr = iter;

				int blockSize = 0;
				foreach ( var linkPtr in ptrList ) {
					linksPtrBufferCPU[iter + blockSize] = linkPtr;
					++blockSize;
				}

				injectionBufferCPU[iter].linksCount = blockSize;
				++iter;
			}

			simulationBufferSrc.SetData(injectionBufferCPU);
			linksBuffer.SetData(linksBufferCPU);
			linksPtrBuffer.SetData(linksPtrBufferCPU);


		}


		/// <summary>
		/// Makes all particles wittingly dead
		/// </summary>
		void ClearParticleBuffer ()
		{
			for (int i=0; i<MaxInjectingParticles; i++) {
				injectionBufferCPU[i].TotalLifeTime = -999999;

			}
			injectionCount = 0;
		}



		/// <summary>
		/// 
		/// </summary>
		/// <param name="disposing"></param>
		protected override void Dispose ( bool disposing )
		{
			if (disposing) {
				paramsCB.Dispose();

				injectionBuffer.Dispose();
				simulationBufferSrc.Dispose();
				simulationBufferDst.Dispose();
				linksBuffer.Dispose();
				linksPtrBuffer.Dispose();
			}
			base.Dispose( disposing );
		}



		/// <summary>
		/// 
		/// </summary>
		/// <param name="gameTime"></param>
		public override void Update ( GameTime gameTime )
		{
			base.Update( gameTime );

			var ds = Game.GetService<DebugStrings>();

			ds.Add( Color.Yellow, "Total particles DST: {0}", simulationBufferDst.GetStructureCount() );
			ds.Add( Color.Yellow, "Total particles SRC: {0}", simulationBufferSrc.GetStructureCount() );
			ds.Add( Color.Yellow, "Injection count: {0}", injectionCount );
		}




		/// <summary>
		/// 
		/// </summary>
		void SwapParticleBuffers ()
		{
			var temp = simulationBufferDst;
			simulationBufferDst = simulationBufferSrc;
			simulationBufferSrc = temp;
		}



		/// <summary>
		/// 
		/// </summary>
		/// <param name="gameTime"></param>
		/// <param name="stereoEye"></param>
		public override void Draw ( GameTime gameTime, Fusion.Graphics.StereoEye stereoEye )
		{
			var device	=	Game.GraphicsDevice;
			var cam = Game.GetService<Camera>();

			int	w	=	device.Viewport.Width;
			int h	=	device.Viewport.Height;

			Params param = new Params();

			param.View			=	cam.ViewMatrix;
			param.Projection	=	cam.ProjMatrix;
			param.MaxParticles	=	0;
			param.DeltaTime		=	gameTime.ElapsedSec;
			param.LinkSize			=	linkSize;


			device.SetCSConstant( 0, paramsCB );
			device.SetVSConstant( 0, paramsCB );
			device.SetGSConstant( 0, paramsCB );
			device.SetPSConstant( 0, paramsCB );
			
			device.SetPSSamplerState( 0, SamplerState.LinearWrap );


			//	Inject : --------------------------------------------------------------------------
			//
	//		simulationBufferSrc.SetData( injectionBufferCPU );
	//		injectionBuffer.SetData( injectionBufferCPU );
			

			device.SetCSResource( 1, simulationBufferSrc );
	//		device.SetCSRWBuffer( 0, simulationBufferSrc, MaxInjectingParticles );

			param.MaxParticles	=	injectionCount;
			paramsCB.SetData( param );
			device.SetCSConstant( 0, paramsCB );


			shader.SetComputeShader( (int)Flags.INJECTION|(int)cfg.IType );
		
			device.Dispatch( MathUtil.IntDivUp( MaxInjectingParticles, BlockSize ) );

//			ClearParticleBuffer();
			// ------------------------------------------------------------------------------------

			
			//	Simulate : ------------------------------------------------------------------------
			//

			if ( state == State.RUN ) {

				// calculate accelerations: ---------------------------------------------------
				device.SetCSRWBuffer( 0, simulationBufferSrc, MaxSimulatedParticles );
				device.SetCSResource( 3, linksPtrBuffer );
				device.SetCSResource( 4, linksBuffer );

				param.MaxParticles	=	MaxSimulatedParticles;
				paramsCB.SetData( param );
				device.SetCSConstant( 0, paramsCB );

				shader.SetComputeShader( (int)Flags.SIMULATION|(int)cfg.IType );
				device.Dispatch( MathUtil.IntDivUp( MaxSimulatedParticles, BlockSize ) );//*/
				shader.ResetComputeShader();


				// move particles: ------------------------------------------------------------
				device.SetCSRWBuffer( 0, simulationBufferSrc, MaxSimulatedParticles );
				device.SetCSConstant( 0, paramsCB );
				shader.SetComputeShader( (int)Flags.MOVE|(int)cfg.IType );
				device.Dispatch( MathUtil.IntDivUp( MaxSimulatedParticles, BlockSize ) );//*/
				shader.ResetComputeShader();



			}
			// ------------------------------------------------------------------------------------


			//	Render: ---------------------------------------------------------------------------
			//
			
			// draw points: ------------------------------------------------------------------------
			shader.SetVertexShader( 0 );
			shader.SetPixelShader( (int)Flags.POINT );
			shader.SetGeometryShader( (int)Flags.POINT );

			device.SetPSResource( 0, texture );
			device.SetCSRWBuffer( 0, null );
			device.SetGSResource( 1, simulationBufferSrc );

			device.SetRasterizerState( RasterizerState.CullNone );

			device.SetBlendState( BlendState.Additive );
			device.SetDepthStencilState( DepthStencilState.Readonly );

			device.Draw( Primitive.PointList, MaxSimulatedParticles, 0 );

			// draw lines: --------------------------------------------------------------------------
			shader.SetPixelShader( (int)Flags.LINE );
			shader.SetGeometryShader( (int)Flags.LINE );
			device.SetGSResource( 1, simulationBufferSrc );
			device.SetGSResource( 5, linksPtrBuffer );
			device.SetGSResource( 6, linksBuffer );
			device.Draw( Primitive.PointList, MaxSimulatedParticles, 0 );
			// --------------------------------------------------------------------------------------


			/*var testSrc = new Particle[MaxSimulatedParticles];
			var testDst = new Particle[MaxSimulatedParticles];

			simulationBufferSrc.GetData( testSrc );
			simulationBufferDst.GetData( testDst );*/

			var debStr = Game.GetService<DebugStrings>();

			debStr.Add("deltaT = " + gameTime.ElapsedSec );
			debStr.Add("Press Z to start simulation");
			debStr.Add("Press Q to pause/unpause");


			base.Draw( gameTime, stereoEye );
		}

	}
}

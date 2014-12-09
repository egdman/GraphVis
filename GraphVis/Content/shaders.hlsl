
#if 0
$compute INJECTION|SIMULATION|MOVE EULER|RUNGE_KUTTA
$geometry POINT|LINE
$pixel POINT|LINE
$vertex
#endif

#define BLOCK_SIZE	512


struct PARTICLE3D {
	float4	Position; // 3 coordinates + mass
	float3	Velocity;
	float4	Color0;
	float	Size0;
	float	TotalLifeTime;
	float	LifeTime;
	int		LinkedTo1;
	int		LinkedTo2;
	float3	Acceleration;
};


struct PARAMS {
	float4x4	View;
	float4x4	Projection;
	int			MaxParticles;
	float		DeltaTime;
	float		LinkSize;
};

cbuffer CB1 : register(b0) { 
	PARAMS Params; 
};

SamplerState						Sampler				: 	register(s0);
Texture2D							Texture 			: 	register(t0);

StructuredBuffer<PARTICLE3D>		GSResourceBuffer	:	register(u1);

RWStructuredBuffer<PARTICLE3D>		particleBufferSrc	: 	register(u0);
RWStructuredBuffer<PARTICLE3D>		particleBufferSrc2	:	register(u2);

//AppendStructuredBuffer<PARTICLE3D>	particleBufferDst	: 	register(u0);

// group shared array for body coordinates:
groupshared float4 shPositions[BLOCK_SIZE];

/*-----------------------------------------------------------------------------
	Simulation :
-----------------------------------------------------------------------------*/


struct BodyState
{
	float4 Position;
	float3 Velocity;
	float3 Acceleration;
	uint id;
};


struct Derivative
{
	float3 dxdt;
	float3 dvdt;
};



float3 SpringForce( in float4 bodyState, in float4 otherBodyState )
{
	float3 R			= otherBodyState.xyz - bodyState.xyz;			
//	float softenerSq	= 0.1f; 
	float Rsquared		= R.x * R.x + R.y * R.y + R.z * R.z + 0.1f;
	float Rabs			= sqrt( Rsquared );
	float Rsixth		= Rsquared * Rsquared * Rsquared;
	float invRCubed		=  0.1f * ( Rabs - Params.LinkSize ) / ( bodyState.w * Rabs );
	return mul( invRCubed, R );

}


float3 Repulsion( in float4 bodyState, in float4 otherBodyState )
{
	float3 R			= otherBodyState.xyz - bodyState.xyz;			
//	float softenerSq	= 0.1f; 
	float Rsquared		= R.x * R.x + R.y * R.y + R.z * R.z + 0.1f;
	float Rabs			= sqrt( Rsquared );
	float Rsixth		= Rsquared * Rsquared * Rsquared;
	float invRCubed		= - 10000.0f * otherBodyState.w / sqrt( Rsixth );
	return mul( invRCubed, R );

}



float3 Acceleration( in PARTICLE3D prt, in int totalNum  )
{
	float3 acc = {0,0,0};
	PARTICLE3D other = particleBufferSrc[ prt.LinkedTo1 ];
	acc += SpringForce( prt.Position, other.Position );
	other = particleBufferSrc[ prt.LinkedTo2 ];
	acc += SpringForce( prt.Position, other.Position );

	[allow_uav_condition] for ( int i = 0; i < totalNum; ++i ) {
		other = particleBufferSrc[ i ];
		acc += Repulsion( prt.Position, other.Position );
	}
	acc -= mul ( prt.Velocity, 1.6f );


	return acc;
}




void IntegrateEUL_SHARED( inout BodyState state, in float dt, in uint threadIndex, in uint numParticles )
{
	
	state.Acceleration	= Acceleration( particleBufferSrc[state.id], numParticles );
}



[numthreads( BLOCK_SIZE, 1, 1 )]
void CSMain( 
	uint3 groupID			: SV_GroupID,
	uint3 groupThreadID 	: SV_GroupThreadID, 
	uint3 dispatchThreadID 	: SV_DispatchThreadID,
	uint  groupIndex 		: SV_GroupIndex
)
{
	int id = dispatchThreadID.x;

#ifdef INJECTION
	if (id < Params.MaxParticles) {
		PARTICLE3D p = particleBufferSrc[id];
		
		if (p.LifeTime < p.TotalLifeTime) {
			
			particleBufferSrc[id] = p;
		}
	}
#endif

#ifdef SIMULATION
	if (id < Params.MaxParticles) {
		PARTICLE3D p = particleBufferSrc[ id ];
		
		if (p.LifeTime < p.TotalLifeTime) {
			p.LifeTime += Params.DeltaTime;

			uint numParticles	=	0;
			uint stride			=	0;
			particleBufferSrc.GetDimensions( numParticles, stride );


			BodyState state;
			state.Position		=	p.Position;
			state.Velocity		=	p.Velocity;
			state.Acceleration	=	p.Acceleration;
			state.id			=	id;

#ifdef EULER

			IntegrateEUL_SHARED( state, Params.DeltaTime, groupIndex, Params.MaxParticles );

#endif
#ifdef RUNGE_KUTTA
	
			IntegrateEUL_SHARED( state, Params.DeltaTime, groupIndex, Params.MaxParticles );

#endif

			float accel	=	length( state.Acceleration );

			float maxAccel = 150.0f;
			accel = saturate( accel / maxAccel );

			p.Color0	=	float4( accel, - 0.5f * accel +1.0f, - 0.5f * accel +1.0f, 1 );

			p.Acceleration = state.Acceleration;

			particleBufferSrc[id] = p;
		}
	}
#endif
#ifdef MOVE
	if (id < Params.MaxParticles) {
		PARTICLE3D p = particleBufferSrc[ id ];
		
		p.Position.xyz += mul( p.Velocity, Params.DeltaTime );
		p.Velocity += mul( p.Acceleration, Params.DeltaTime );
		particleBufferSrc[ id ] = p;
	}
#endif


}







/*-----------------------------------------------------------------------------
	Rendering :
-----------------------------------------------------------------------------*/
/*

struct VSOutput {
	float4	Position		:	POSITION;
	float4	Color0			:	COLOR0;

	float	Size0			:	PSIZE;

	float	TotalLifeTime	:	TEXCOORD0;
	float	LifeTime		:	TEXCOORD1;
};*/


struct VSOutput {
int vertexID : TEXCOORD0;
};

struct GSOutput {
	float4	Position : SV_Position;
	float2	TexCoord : TEXCOORD0;
	float4	Color    : COLOR0;
};

/*
VSOutput VSMain( uint vertexID : SV_VertexID )
{
	PARTICLE prt = particleBufferSrc[ vertexID ];
	VSOutput output;

	output.Color0			=	prt.Color1;

	output.Size0			=	prt.Size0;
	
	output.TotalLifeTime	=	prt.TotalLifeTime;
	output.LifeTime			=	prt.LifeTime;

	output.Position			=	float4(prt.Position, 0, 1);

	return output;
}*/


VSOutput VSMain( uint vertexID : SV_VertexID )
{
VSOutput output;
output.vertexID = vertexID;
return output;
}


float Ramp(float f_in, float f_out, float t) 
{
	float y = 1;
	t = saturate(t);
	
	float k_in	=	1 / f_in;
	float k_out	=	-1 / (1-f_out);
	float b_out =	-k_out;	
	
	if (t<f_in)  y = t * k_in;
	if (t>f_out) y = t * k_out + b_out;
	
	
	return y;
}


#ifdef POINT
[maxvertexcount(6)]
void GSMain( point VSOutput inputPoint[1], inout TriangleStream<GSOutput> outputStream )
{

	GSOutput p0, p1, p2, p3;
	
//	VSOutput prt = inputPoint[0];

	PARTICLE3D prt = GSResourceBuffer[ inputPoint[0].vertexID ];
	
	if (prt.LifeTime >= prt.TotalLifeTime ) {
		return;
	}
	

		float factor = saturate(prt.LifeTime / prt.TotalLifeTime);

//		float sz = lerp( prt.Size0, prt.Size1, factor )/2;

		float sz = prt.Size0;

		float time = prt.LifeTime;

		float4 color	=	prt.Color0;

		float4 pos		=	float4( prt.Position.xyz, 1 );

		float4 posV		=	mul( pos, Params.View );

//		p0.Position = mul( float4( position + float2( sz, sz), 0, 1 ), Params.Projection );
		p0.Position = mul( posV + float4( sz, sz, 0, 0 ) , Params.Projection );
//		p0.Position = posP + float4( sz, sz, 0, 0 );		
		p0.TexCoord = float2(1,1);
		p0.Color = color;

//		p1.Position = mul( float4( position + float2(-sz, sz), 0, 1 ), Params.Projection );
		p1.Position = mul( posV + float4(-sz, sz, 0, 0 ) , Params.Projection );
//		p1.Position = posP + float4(-sz, sz, 0, 0 );
		p1.TexCoord = float2(0,1);
		p1.Color = color;

//		p2.Position = mul( float4( position + float2(-sz,-sz), 0, 1 ), Params.Projection );
		p2.Position = mul( posV + float4(-sz,-sz, 0, 0 ) , Params.Projection );
//		p2.Position = posP + float4(-sz,-sz, 0, 0 );
		p2.TexCoord = float2(0,0);
		p2.Color = color;

//		p3.Position = mul( float4( position + float2( sz,-sz), 0, 1 ), Params.Projection );
		p3.Position = mul( posV + float4( sz,-sz, 0, 0 ) , Params.Projection );
//		p3.Position = posP + float4( sz,-sz, 0, 0 );
		p3.TexCoord = float2(1,0);
		p3.Color = color;

		outputStream.Append(p0);
		outputStream.Append(p1);
		outputStream.Append(p2);
		outputStream.RestartStrip();
		outputStream.Append(p0);
		outputStream.Append(p2);
		outputStream.Append(p3);
		outputStream.RestartStrip();

}


#endif

#ifdef LINE
[maxvertexcount(2)]
void GSMain( line VSOutput inputLine[2], inout LineStream<GSOutput> outputStream )
{
	GSOutput p1, p2;

	PARTICLE3D end1 = GSResourceBuffer[ inputLine[0].vertexID ];
	PARTICLE3D end2 = GSResourceBuffer[ inputLine[1].vertexID ];

	float4 pos1		=	float4( end1.Position.xyz, 1 );
	float4 pos2		=	float4( end2.Position.xyz, 1 );

	float4 posV1	=	mul( pos1, Params.View );
	float4 posV2	=	mul( pos2, Params.View );

	p1.Position		=	mul( posV1, Params.Projection );
	p2.Position		=	mul( posV2, Params.Projection );

	p1.TexCoord		=	float2(0, 0);
	p2.TexCoord		=	float2(0, 0);

	p1.Color		=	end1.Color0;
	p2.Color		=	end2.Color0;

	outputStream.Append(p1);
	outputStream.Append(p2);
	outputStream.RestartStrip();
}

#endif

#ifdef LINE
float4 PSMain( GSOutput input ) : SV_Target
{
	return float4(input.Color.rgb,1);
}
#endif

#ifdef POINT
float4 PSMain( GSOutput input ) : SV_Target
{
	return Texture.Sample( Sampler, input.TexCoord ) * float4(input.Color.rgb,1);
}
#endif



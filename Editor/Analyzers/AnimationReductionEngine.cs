using System;
using System.Collections.Generic;

namespace ApexForge.ProjectOptimizer
{
    /// <summary>
    /// Pure managed animation reduction engine. It deliberately contains no UnityEngine/UnityEditor
    /// references so the expensive curve reduction can run on ProjectOptimizer's worker threads.
    /// Unity adapters convert AnimationCurve/EditorCurveBinding values into these immutable managed records.
    /// </summary>
    public static class ManagedAnimationReductionEngine
    {
        public enum ManagedTangentMode { Free = 0, Auto = 1, Constant = 2, Linear = 3, ClampedAuto = 4 }
        public enum ManagedWeightedMode { None = 0, In = 1, Out = 2, Both = 3 }
        public enum CurveKind { Generic, Position, Rotation, Scale }

        public sealed class ManagedKeyframe
        {
            public float time, value, inTangent, outTangent, inWeight, outWeight;
            public ManagedWeightedMode weightedMode;
            public ManagedTangentMode LeftMode, RightMode;
            public bool Broken;

            public ManagedKeyframe Copy() => (ManagedKeyframe)MemberwiseClone();
        }

        public sealed class ManagedAnimationCurve
        {
            readonly ManagedKeyframe[] m_Keys;
            public int PreWrapMode { get; }
            public int PostWrapMode { get; }
            public ManagedKeyframe[] keys => m_Keys;
            public int length => m_Keys.Length;
            public ManagedAnimationCurve(ManagedKeyframe[] keys, int preWrapMode = 0, int postWrapMode = 0)
            { m_Keys = keys ?? Array.Empty<ManagedKeyframe>(); PreWrapMode = preWrapMode; PostWrapMode = postWrapMode; }
            public ManagedAnimationCurve(ManagedKeyframe[] keys, ManagedTangentMode[] left, ManagedTangentMode[] right, bool[] broken, int preWrapMode, int postWrapMode)
            { m_Keys = keys ?? Array.Empty<ManagedKeyframe>(); for(int i=0;i<m_Keys.Length;i++){m_Keys[i].LeftMode=left!=null&&i<left.Length?left[i]:ManagedTangentMode.Free;m_Keys[i].RightMode=right!=null&&i<right.Length?right[i]:ManagedTangentMode.Free;m_Keys[i].Broken=broken!=null&&i<broken.Length&&broken[i];} PreWrapMode=preWrapMode; PostWrapMode=postWrapMode; }
            public ManagedKeyframe this[int index] => m_Keys[index];
            public ManagedAnimationCurve Copy(){var keys=new ManagedKeyframe[m_Keys.Length];for(int i=0;i<keys.Length;i++)keys[i]=m_Keys[i].Copy();return new ManagedAnimationCurve(keys,PreWrapMode,PostWrapMode);}
            public ManagedTangentMode GetLeftTangentMode(int i)=>m_Keys[i].LeftMode;
            public ManagedTangentMode GetRightTangentMode(int i)=>m_Keys[i].RightMode;
            public bool GetBroken(int i)=>m_Keys[i].Broken;
            public bool IsFinite(){for(int i=0;i<m_Keys.Length;i++){var k=m_Keys[i];if(float.IsNaN(k.time)||float.IsInfinity(k.time)||float.IsNaN(k.value)||float.IsInfinity(k.value)||float.IsNaN(k.inTangent)||float.IsInfinity(k.inTangent)||float.IsNaN(k.outTangent)||float.IsInfinity(k.outTangent))return false;}return true;}
            public bool IsConstant(out float value){value=0f;if(m_Keys.Length==0)return false;value=m_Keys[0].value;for(int i=1;i<m_Keys.Length;i++)if(m_Keys[i].value!=value)return false;return true;}
            public bool IsSafeConstant(){if(!IsConstant(out _))return false;for(int i=0;i<m_Keys.Length-1;i++){var r=m_Keys[i].RightMode;var l=m_Keys[i+1].LeftMode;if(r==ManagedTangentMode.Constant||l==ManagedTangentMode.Constant|| (ManagedMath.Approximately(m_Keys[i].outTangent,0f)&&ManagedMath.Approximately(m_Keys[i+1].inTangent,0f)))continue;return false;}return true;}
            public bool HasWeightedKeys(){for(int i=0;i<m_Keys.Length;i++) if(m_Keys[i].weightedMode!=ManagedWeightedMode.None) return true; return false;}
            public float Evaluate(float time){if(m_Keys.Length==0)return 0f;if(m_Keys.Length==1)return m_Keys[0].value;if(time<=m_Keys[0].time)return m_Keys[0].value;if(time>=m_Keys[m_Keys.Length-1].time)return m_Keys[m_Keys.Length-1].value;int lo=0,hi=m_Keys.Length-1;while(lo+1<hi){int mid=(lo+hi)>>1;if(m_Keys[mid].time<=time)lo=mid;else hi=mid;}return ManagedAnimationReductionEngine.EvaluateTwoKeySegment(m_Keys[lo],m_Keys[lo+1],time);}
            public ManagedAnimationCurve RemoveKey(int index){if(index<=0||index>=m_Keys.Length-1)return Copy();var keys=new ManagedKeyframe[m_Keys.Length-1];Array.Copy(m_Keys,0,keys,0,index);Array.Copy(m_Keys,index+1,keys,index,m_Keys.Length-index-1);return new ManagedAnimationCurve(keys,PreWrapMode,PostWrapMode);}
        }

        public sealed class ManagedCurveWork
        {
            public string Path, PropertyName, TypeName;
            public CurveKind Kind;
            public ManagedAnimationCurve Curve;
            public float Tolerance;
        }
        public sealed class ManagedCurveReduction { public string BindingKey; public ManagedAnimationCurve Reduced; public int RemovedKeys; }
        public sealed class ManagedReductionResult { public readonly List<ManagedCurveReduction> Curves = new List<ManagedCurveReduction>(); public int RemovedKeys; }

        static class ManagedMath
        {
            public static float Abs(float v)=>Math.Abs(v); public static float Max(float a,float b)=>Math.Max(a,b); public static int Max(int a,int b)=>Math.Max(a,b); public static float Min(float a,float b)=>Math.Min(a,b); public static int Min(int a,int b)=>Math.Min(a,b); public static float Sqrt(float v)=>(float)Math.Sqrt(v); public static float Lerp(float a,float b,float t)=>a+(b-a)*t; public static float Clamp01(float v)=>v<0f?0f:v>1f?1f:v; public static float Clamp(float v,float a,float b)=>v<a?a:v>b?b:v; public static int Clamp(int v,int a,int b)=>v<a?a:v>b?b:v; public static int CeilToInt(float v)=>(int)Math.Ceiling(v); public static bool Approximately(float a,float b)=>Abs(a-b)<=1e-6f*Max(1f,Max(Abs(a),Abs(b)));
        }

        readonly struct ManagedVector3
        {
            public readonly float x, y, z;
            public ManagedVector3(float x, float y, float z){this.x=x;this.y=y;this.z=z;}
        }

        readonly struct ManagedVector4
        {
            public readonly float x, y, z, w;
            public ManagedVector4(float x, float y, float z, float w){this.x=x;this.y=y;this.z=z;this.w=w;}
        }

        readonly struct ManagedQuaternion
        {
            public readonly float x, y, z, w;
            public static ManagedQuaternion identity => new ManagedQuaternion(0f,0f,0f,1f);
            public ManagedQuaternion(float x, float y, float z, float w){this.x=x;this.y=y;this.z=z;this.w=w;}

            static ManagedQuaternion Multiply(ManagedQuaternion lhs, ManagedQuaternion rhs)
            {
                return new ManagedQuaternion(
                    lhs.w*rhs.x + lhs.x*rhs.w + lhs.y*rhs.z - lhs.z*rhs.y,
                    lhs.w*rhs.y - lhs.x*rhs.z + lhs.y*rhs.w + lhs.z*rhs.x,
                    lhs.w*rhs.z + lhs.x*rhs.y - lhs.y*rhs.x + lhs.z*rhs.w,
                    lhs.w*rhs.w - lhs.x*rhs.x - lhs.y*rhs.y - lhs.z*rhs.z);
            }

            static ManagedQuaternion AngleAxis(float degrees, float ax, float ay, float az)
            {
                double radians = degrees * (Math.PI / 180.0);
                double half = radians * 0.5;
                float s = (float)Math.Sin(half);
                float c = (float)Math.Cos(half);
                return new ManagedQuaternion(ax*s, ay*s, az*s, c);
            }

            /// <summary>Matches Unity's ZXY Euler rotation order used by Quaternion.Euler.</summary>
            public static ManagedQuaternion Euler(ManagedVector3 euler)
            {
                ManagedQuaternion z = AngleAxis(euler.z,0f,0f,1f);
                ManagedQuaternion x = AngleAxis(euler.x,1f,0f,0f);
                ManagedQuaternion y = AngleAxis(euler.y,0f,1f,0f);
                return Multiply(Multiply(y,x),z).Normalized();
            }

            ManagedQuaternion Normalized()
            {
                float magnitude = (float)Math.Sqrt(x*x + y*y + z*z + w*w);
                return magnitude <= 1e-7f ? identity : new ManagedQuaternion(x/magnitude,y/magnitude,z/magnitude,w/magnitude);
            }

            public static float Angle(ManagedQuaternion a, ManagedQuaternion b)
            {
                a = a.Normalized(); b = b.Normalized();
                float dot = ManagedMath.Clamp(Math.Abs(a.x*b.x + a.y*b.y + a.z*b.z + a.w*b.w), 0f, 1f);
                return (float)(2.0 * Math.Acos(dot) * (180.0 / Math.PI));
            }
        }

        struct ReductionCandidate : IComparable<ReductionCandidate>{public int Index,Version;public float Priority;public bool Exact;public ReductionCandidate(int i,int v,float p,bool e){Index=i;Version=v;Priority=p;Exact=e;}public int CompareTo(ReductionCandidate o){int c=Priority.CompareTo(o.Priority);if(c!=0)return c;if(Exact!=o.Exact)return Exact?-1:1;return Index.CompareTo(o.Index);}}
        sealed class MinHeap{readonly List<ReductionCandidate> m=new List<ReductionCandidate>();public int Count=>m.Count;public void Push(ReductionCandidate x){int i=m.Count;m.Add(x);while(i>0){int p=(i-1)>>1;if(m[p].CompareTo(m[i])<=0)break;var swap=m[p];m[p]=m[i];m[i]=swap;i=p;}}public ReductionCandidate Pop(){if(m.Count==0)throw new InvalidOperationException();var r=m[0];int l=m.Count-1;if(l==0){m.RemoveAt(0);return r;}m[0]=m[l];m.RemoveAt(l);int i=0;while(true){int a=(i<<1)+1;if(a>=m.Count)break;int b=a+1, q=b<m.Count&&m[b].CompareTo(m[a])<0?b:a;if(m[i].CompareTo(m[q])<=0)break;var swap=m[i];m[i]=m[q];m[q]=swap;i=q;}return r;}}
        struct Cubic{public float C0,C1,C2,C3;}
        struct CandidateEvaluation{public bool Valid,UsedRefit;public float Error,OutTangent,InTangent;}
        sealed class SharedKeyCandidate{public float Time;public int[] Indices;}
        sealed class CurveReductionPlan{public List<int> RemovedIndices;public ManagedKeyframe[] NewKeys;public ManagedTangentMode[] LeftModes,RightModes;public bool[] Broken;public float[] InTangents,OutTangents;public float ErrorTolerance;}
        sealed class CurveWork{public string Path,PropertyName,TypeName;public ManagedAnimationCurve Original,Reduced;public CurveKind Kind;public float Tolerance,RelativeRatio;}
        sealed class ActiveCurveState
        {
            public readonly ManagedKeyframe[] Keys;public readonly ManagedTangentMode[] LeftModes,RightModes;public readonly bool[] Broken;public readonly int[] Previous,Next,Versions;public readonly bool[] Alive,RefittedIn,RefittedOut,WeightedBoundary,SteppedBoundary;public readonly float[] EffectiveIn,EffectiveOut;public readonly Cubic[] SourceCubics;
            public ActiveCurveState(ManagedAnimationCurve curve){Keys=curve.keys;int n=Keys.Length;LeftModes=new ManagedTangentMode[n];RightModes=new ManagedTangentMode[n];Broken=new bool[n];Previous=new int[n];Next=new int[n];Versions=new int[n];Alive=new bool[n];EffectiveIn=new float[n];EffectiveOut=new float[n];RefittedIn=new bool[n];RefittedOut=new bool[n];WeightedBoundary=new bool[ManagedMath.Max(0,n-1)];SteppedBoundary=new bool[ManagedMath.Max(0,n-1)];SourceCubics=new Cubic[ManagedMath.Max(0,n-1)];for(int i=0;i<n;i++){LeftModes[i]=Keys[i].LeftMode;RightModes[i]=Keys[i].RightMode;Broken[i]=Keys[i].Broken;Previous[i]=i-1;Next[i]=i+1<n?i+1:-1;Alive[i]=true;EffectiveIn[i]=Keys[i].inTangent;EffectiveOut[i]=Keys[i].outTangent;}if(n>0){Previous[0]=-1;Next[n-1]=-1;}for(int i=0;i<n-1;i++){SteppedBoundary[i]=RightModes[i]==ManagedTangentMode.Constant||LeftModes[i+1]==ManagedTangentMode.Constant;WeightedBoundary[i]=IsWeightedOut(Keys[i])||IsWeightedIn(Keys[i+1]);SourceCubics[i]=CubicFromKeyPair(Keys[i],Keys[i+1]);}}
        }
        const int k_FallbackMaxValidationDepth=8,k_FitSteps=24;const float k_TimeEpsilon=1e-5f,k_ErrorComfortRatio=0.25f;

		static CurveReductionPlan ComputeReductionPlan(ManagedAnimationCurve curve, float tolerance, bool allowTangentRefit)
		{
			if (curve == null || curve.length < 3 || tolerance <= 0f || HasNonFiniteKeys(curve))
				return null;

			// Common authored curves need no priority-queue machinery. A constant or straight linear
			// curve can collapse to its endpoints after a single exact validation.
			if (IsEffectivelyLinearCurve(curve, tolerance, out _))
			{
				ManagedKeyframe[] keys = curve.keys;
				var directKept = new List<ManagedKeyframe>(2) { keys[0], keys[keys.Length - 1] };
				var directPlan = BuildDirectPlan(curve, directKept, tolerance);
				if (directPlan != null) return directPlan;
			}

			ActiveCurveState state = new ActiveCurveState(curve);
			int n = state.Keys.Length;
			var heap = new MinHeap();

			// The initial heap contains cheap lower bounds. Expensive exact span errors are evaluated
			// lazily only when a candidate reaches the top of the heap. This is the key performance
			// improvement over the former all-pairs search.
			for (int i = 1; i < n - 1; i++)
				QueueRemovalCandidate(curve, state, i, tolerance, allowTangentRefit, heap, false);

			var removed = new List<int>(ManagedMath.Min(n / 2, n - 2));

			while (heap.Count > 0)
			{
				ReductionCandidate candidate = heap.Pop();
				int index = candidate.Index;
				if (!IsCurrentCandidate(candidate, state))
					continue;

				// A non-exact priority is only a lower bound. Resolve it once before making a decision.
				if (!candidate.Exact)
				{
					QueueRemovalCandidate(curve, state, index, tolerance, allowTangentRefit, heap, true);
					continue;
				}

				// Because the heap contains the current minimum exact/lower-bound cost, once this is
				// above tolerance no remaining candidate can be safely removed at the current state.
				if (candidate.Priority > tolerance)
					break;

				int left = state.Previous[index];
				int right = state.Next[index];
				if (left < 0 || right < 0)
					continue;

				CandidateEvaluation evaluation = EvaluateRemoval(curve, state, index, left, right,
					tolerance, allowTangentRefit, true);
				if (!evaluation.Valid || evaluation.Error > tolerance)
				{
					// This should be rare because the candidate was exact when queued, but keeping the
					// defensive path makes stale/edge-state transitions safe.
					continue;
				}

				state.Alive[index] = false;
				state.Previous[right] = left;
				state.Next[left] = right;
				removed.Add(index);

				// The newly created edge owns its endpoint tangents. Only those two tangent directions
				// change; the opposite sides of the endpoint keys remain untouched.
				state.EffectiveOut[left] = state.Keys[left].outTangent;
				state.EffectiveIn[right] = state.Keys[right].inTangent;
				state.RefittedOut[left] = false;
				state.RefittedIn[right] = false;
				if (evaluation.UsedRefit)
				{
					state.EffectiveOut[left] = evaluation.OutTangent;
					state.EffectiveIn[right] = evaluation.InTangent;
					state.RefittedOut[left] = true;
					state.RefittedIn[right] = true;
				}

				// Only candidates adjacent to the removed key have changed neighborhoods.
				if (left > 0 && state.Alive[left])
				{
					state.Versions[left]++;
					QueueRemovalCandidate(curve, state, left, tolerance, allowTangentRefit, heap, false);
				}
				if (right < n - 1 && state.Alive[right])
				{
					state.Versions[right]++;
					QueueRemovalCandidate(curve, state, right, tolerance, allowTangentRefit, heap, false);
				}
			}

			if (removed.Count == 0)
				return null;

			var kept = new List<int>(n - removed.Count);
			for (int i = 0; i < n; i++)
				if (state.Alive[i]) kept.Add(i);
			if (kept.Count >= n)
				return null;

			var newKeys = new ManagedKeyframe[kept.Count];
			var leftModes = new ManagedTangentMode[kept.Count];
			var rightModes = new ManagedTangentMode[kept.Count];
			var broken = new bool[kept.Count];
			var inTangents = new float[kept.Count];
			var outTangents = new float[kept.Count];
			for (int i = 0; i < kept.Count; i++)
			{
				int source = kept[i];
				newKeys[i] = state.Keys[source];
				leftModes[i] = state.LeftModes[source];
				rightModes[i] = state.RightModes[source];
				broken[i] = state.Broken[source];
				inTangents[i] = state.EffectiveIn[source];
				outTangents[i] = state.EffectiveOut[source];
				if (state.RefittedIn[source])
				{
					leftModes[i] = ManagedTangentMode.Free;
					broken[i] = true;
				}
				if (state.RefittedOut[source])
				{
					rightModes[i] = ManagedTangentMode.Free;
					broken[i] = true;
				}
			}

			for (int i = 0; i < newKeys.Length; i++)
			{
				newKeys[i].inTangent = inTangents[i];
				newKeys[i].outTangent = outTangents[i];
			}

			removed.Sort();
			var plan = new CurveReductionPlan
			{
				RemovedIndices = removed,
				NewKeys = newKeys,
				LeftModes = leftModes,
				RightModes = rightModes,
				Broken = broken,
				InTangents = inTangents,
				OutTangents = outTangents,
				ErrorTolerance = tolerance
			};

			ManagedAnimationCurve rebuilt = BuildCurveFromPlan(curve, plan);
			return CurveStaysWithinTolerance(curve, rebuilt, tolerance) ? plan : null;
		}

		static bool IsEffectivelyLinearCurve(ManagedAnimationCurve curve, float tolerance, out float maxError)
		{
			maxError = 0f;
			if (curve == null || curve.length < 3) return false;
			ManagedKeyframe[] keys = curve.keys;
			for (int i = 0; i < keys.Length - 1; i++)
			{
				if (keys[i].weightedMode != ManagedWeightedMode.None || keys[i + 1].weightedMode != ManagedWeightedMode.None) return false;
				if (curve.GetLeftTangentMode(i) == ManagedTangentMode.Constant
					|| curve.GetRightTangentMode(i) == ManagedTangentMode.Constant
					|| curve.GetLeftTangentMode(i + 1) == ManagedTangentMode.Constant) return false;
				float span = keys[i + 1].time - keys[i].time;
				if (span <= k_TimeEpsilon) return false;
			}
			ManagedKeyframe a = keys[0], b = keys[keys.Length - 1];
			float spanAll = b.time - a.time;
			if (spanAll <= k_TimeEpsilon) return false;
			float slope = (b.value - a.value) / spanAll;
			for (int i = 1; i < keys.Length - 1; i++)
			{
				float expected = a.value + slope * (keys[i].time - a.time);
				maxError = ManagedMath.Max(maxError, ManagedMath.Abs(expected - keys[i].value));
				if (maxError > tolerance) return false;
			}
			return true;
		}

		static CurveReductionPlan BuildDirectPlan(ManagedAnimationCurve curve, List<ManagedKeyframe> kept, float tolerance)
		{
			if (curve == null || kept == null || kept.Count != 2) return null;
			ManagedKeyframe[] source = curve.keys;
			ManagedKeyframe[] newKeys = kept.ToArray();
			float span = newKeys[1].time - newKeys[0].time;
			if (span <= k_TimeEpsilon) return null;
			float slope = (newKeys[1].value - newKeys[0].value) / span;
			newKeys[0].outTangent = slope;
			newKeys[1].inTangent = slope;
			var test = new ManagedAnimationCurve(newKeys);
			if (!CurveStaysWithinTolerance(curve, test, tolerance)) return null;
			var removed = new List<int>(ManagedMath.Max(0, source.Length - 2));
			for (int i = 1; i < source.Length - 1; i++) removed.Add(i);
			return new CurveReductionPlan
			{
				RemovedIndices = removed, NewKeys = newKeys,
				LeftModes = new[] { ManagedTangentMode.Free, ManagedTangentMode.Free },
				RightModes = new[] { ManagedTangentMode.Free, ManagedTangentMode.Free },
				Broken = new[] { true, true },
				InTangents = new[] { 0f, slope },
				OutTangents = new[] { slope, 0f },
				ErrorTolerance = tolerance
			};
		}

		static bool IsCurrentCandidate(ReductionCandidate candidate, ActiveCurveState state)
		{
			int index = candidate.Index;
			return index > 0 && index < state.Keys.Length - 1
				&& state.Alive[index]
				&& candidate.Version == state.Versions[index];
		}

		static void QueueRemovalCandidate(ManagedAnimationCurve original, ActiveCurveState state, int index,
			float tolerance, bool allowTangentRefit, MinHeap heap, bool forceExact)
		{
			if (index <= 0 || index >= state.Keys.Length - 1 || !state.Alive[index]) return;
			int left = state.Previous[index], right = state.Next[index];
			if (left < 0 || right < 0) return;
			if (HasForbiddenBoundary(state, left, right)) return;

			CandidateEvaluation fast = EvaluateRemoval(original, state, index, left, right,
				tolerance, allowTangentRefit: false, exact: forceExact);
			if (forceExact)
			{
				CandidateEvaluation best = fast;
				if (allowTangentRefit && fast.Error > tolerance && CanRefit(state, left, right))
				{
					CandidateEvaluation refit = EvaluateRemoval(original, state, index, left, right,
						tolerance, allowTangentRefit: true, exact: true);
					if (refit.Error < best.Error)
						best = refit;
				}
				heap.Push(new ReductionCandidate(index, state.Versions[index], best.Error, true));
				return;
			}

			// Lower-bound phase: exact error is not required. The removed key itself plus three
			// normalized interior samples provide a cheap bound. It can never underestimate the
			// true error because every measured sample is a real point on the candidate span.
			float lowerBound = EstimateRemovalLowerBound(state, left, right, index);
			heap.Push(new ReductionCandidate(index, state.Versions[index], lowerBound, false));
		}

		static bool HasForbiddenBoundary(ActiveCurveState state, int left, int right)
		{
			for (int i = left; i < right && i < state.SteppedBoundary.Length; i++)
				if (state.SteppedBoundary[i] || state.WeightedBoundary[i]) return true;
			return false;
		}

		static float EstimateRemovalLowerBound(ActiveCurveState state, int left, int right, int removedIndex)
		{
			ManagedKeyframe a = GetEffectiveKey(state.Keys[left], state.EffectiveIn, state.EffectiveOut, left);
			ManagedKeyframe b = GetEffectiveKey(state.Keys[right], state.EffectiveIn, state.EffectiveOut, right);
			float span = b.time - a.time;
			if (span <= k_TimeEpsilon) return float.PositiveInfinity;
			Cubic candidate = CubicFromKeyPair(a, b);
			float bound = ManagedMath.Abs(EvaluateCubic(candidate, (state.Keys[removedIndex].time - a.time) / span)
				- state.Keys[removedIndex].value);
			for (int s = 1; s <= 3; s++)
			{
				float u = s * 0.25f;
				float time = a.time + span * u;
				float source = EvaluateKeyArray(state.Keys, left, right, time);
				bound = ManagedMath.Max(bound, ManagedMath.Abs(EvaluateCubic(candidate, u) - source));
			}
			return bound;
		}

		static CandidateEvaluation EvaluateRemoval(ManagedAnimationCurve original, ActiveCurveState state,
			int removedIndex, int left, int right, float tolerance, bool allowTangentRefit, bool exact)
		{
			CandidateEvaluation result = new CandidateEvaluation
			{
				Error = float.PositiveInfinity,
				OutTangent = state.EffectiveOut[left],
				InTangent = state.EffectiveIn[right]
			};

			if (!exact)
			{
				result.Error = EstimateRemovalLowerBound(state, left, right, removedIndex);
				return result;
			}

			ManagedKeyframe candidateLeft = GetEffectiveKey(state.Keys[left], state.EffectiveIn, state.EffectiveOut, left);
			ManagedKeyframe candidateRight = GetEffectiveKey(state.Keys[right], state.EffectiveIn, state.EffectiveOut, right);
			if (AnalyticSpanError(state.Keys, state.SourceCubics, left, right, candidateLeft, candidateRight, tolerance,
				out float directError))
			{
				result.Valid = directError <= tolerance;
				result.Error = directError;
				// Once the authored-tangent version already satisfies the error budget, do not
				// perturb tangents merely to shave a little more numerical error. Preserving the
				// authored tangent representation is the safer and more stable result.
				if (result.Valid || !allowTangentRefit)
					return result;
			}
			else
			{
				// AnalyticSpanError returns false both for an out-of-tolerance edge and for invalid
				// spans; directError still contains the measured maximum in the former case.
				result.Error = directError;
			}

			if (!allowTangentRefit || !CanRefit(state, left, right))
				return result;
			if (!TryFitTangentsFast(original, state.Keys, left, right, out float fitOut, out float fitIn))
				return result;

			ManagedKeyframe fittedLeft = WithOutTangent(candidateLeft, fitOut);
			ManagedKeyframe fittedRight = WithInTangent(candidateRight, fitIn);
			bool fitValid = AnalyticSpanError(state.Keys, state.SourceCubics, left, right, fittedLeft, fittedRight, tolerance,
				out float fitError);
			if (fitError < result.Error)
			{
				result.Error = fitError;
				result.OutTangent = fitOut;
				result.InTangent = fitIn;
				result.UsedRefit = true;
				result.Valid = fitValid && fitError <= tolerance;
			}
			return result;
		}

		static bool CanRefit(ActiveCurveState state, int left, int right)
		{
			if (left < 0 || right <= left) return false;
			if (HasForbiddenBoundary(state, left, right)) return false;
			return !float.IsInfinity(state.Keys[left].outTangent)
				&& !float.IsInfinity(state.Keys[right].inTangent);
		}

		static bool IsWeightedOut(ManagedKeyframe key)
		{
			return key.weightedMode == ManagedWeightedMode.Out || key.weightedMode == ManagedWeightedMode.Both;
		}

		static bool IsWeightedIn(ManagedKeyframe key)
		{
			return key.weightedMode == ManagedWeightedMode.In || key.weightedMode == ManagedWeightedMode.Both;
		}

		static ManagedKeyframe GetEffectiveKey(ManagedKeyframe source, float[] effectiveIn, float[] effectiveOut, int index)
		{
			source.inTangent = effectiveIn[index];
			source.outTangent = effectiveOut[index];
			return source;
		}

		static bool AnalyticSpanError(ManagedKeyframe[] keys, Cubic[] sourceCubics, int start, int end,
			ManagedKeyframe candidateStart, ManagedKeyframe candidateEnd, float tolerance, out float maxError)
		{
			maxError = 0f;
			float candidateSpan = candidateEnd.time - candidateStart.time;
			if (candidateSpan <= k_TimeEpsilon)
			{
				maxError = float.PositiveInfinity;
				return false;
			}

			Cubic candidate = CubicFromKeyPair(candidateStart, candidateEnd);
			for (int k = start + 1; k < end; k++)
			{
				float u = (keys[k].time - candidateStart.time) / candidateSpan;
				float error = ManagedMath.Abs(EvaluateCubic(candidate, u) - keys[k].value);
				if (error > maxError) maxError = error;
				if (error > tolerance) return false;
			}

			// Every source interval in the proposed span is itself a cubic. The difference between
			// two cubics is cubic, so its exact maximum can be found from the quadratic derivative.
			for (int s = start; s < end; s++)
			{
				float t0 = keys[s].time, t1 = keys[s + 1].time;
				if (t1 - t0 <= k_TimeEpsilon) continue;
				Cubic sourceCubic = sourceCubics != null && s < sourceCubics.Length
					? sourceCubics[s] : CubicFromKeyPair(keys[s], keys[s + 1]);
				TryFindMaxCubicDifference(candidate, candidateStart.time, candidateSpan,
					sourceCubic, t0, t1,
					out float error, out _);
				if (error > maxError) maxError = error;
				if (maxError > tolerance) return false;
			}
			return true;
		}

		static Cubic CubicFromKeyPair(ManagedKeyframe a, ManagedKeyframe b)
		{
			float span = b.time - a.time;
			return new Cubic
			{
				C0 = a.value,
				C1 = span * a.outTangent,
				C2 = -3f * a.value + 3f * b.value - 2f * span * a.outTangent - span * b.inTangent,
				C3 = 2f * a.value - 2f * b.value + span * a.outTangent + span * b.inTangent
			};
		}

		static float EvaluateCubic(Cubic c, float u)
		{
			return ((c.C3 * u + c.C2) * u + c.C1) * u + c.C0;
		}

		static Cubic ComposeCubic(Cubic c, float alpha, float beta)
		{
			float a2 = alpha * alpha;
			float a3 = a2 * alpha;
			float b2 = beta * beta;
			float b3 = b2 * beta;
			return new Cubic
			{
				C0 = c.C0 + c.C1 * alpha + c.C2 * a2 + c.C3 * a3,
				C1 = beta * (c.C1 + 2f * c.C2 * alpha + 3f * c.C3 * a2),
				C2 = b2 * (c.C2 + 3f * c.C3 * alpha),
				C3 = b3 * c.C3
			};
		}

		static bool TryFindMaxCubicDifference(Cubic candidate, float candidateStart, float candidateSpan,
			Cubic original, float originalStart, float originalEnd, out float maxError, out float maxTime)
		{
			maxError = 0f;
			maxTime = originalStart;
			float sourceSpan = originalEnd - originalStart;
			if (sourceSpan <= k_TimeEpsilon || candidateSpan <= k_TimeEpsilon) return false;

			float alpha = (originalStart - candidateStart) / candidateSpan;
			float beta = sourceSpan / candidateSpan;
			Cubic c = ComposeCubic(candidate, alpha, beta);

			float d0 = c.C0 - original.C0;
			float d1 = c.C1 - original.C1;
			float d2 = c.C2 - original.C2;
			float d3 = c.C3 - original.C3;

			EvaluateDifferenceAt(d0, d1, d2, d3, 0f, originalStart, sourceSpan, ref maxError, ref maxTime);
			EvaluateDifferenceAt(d0, d1, d2, d3, 1f, originalStart, sourceSpan, ref maxError, ref maxTime);

			float qa = 3f * d3;
			float qb = 2f * d2;
			float qc = d1;
			if (ManagedMath.Abs(qa) <= 1e-12f)
			{
				if (ManagedMath.Abs(qb) > 1e-12f)
				{
					float root = -qc / qb;
					if (root > 0f && root < 1f)
						EvaluateDifferenceAt(d0, d1, d2, d3, root, originalStart, sourceSpan, ref maxError, ref maxTime);
				}
			}
			else
			{
				float discriminant = qb * qb - 4f * qa * qc;
				if (discriminant >= 0f)
				{
					float root = ManagedMath.Sqrt(discriminant);
					float r0 = (-qb - root) / (2f * qa);
					float r1 = (-qb + root) / (2f * qa);
					if (r0 > 0f && r0 < 1f)
						EvaluateDifferenceAt(d0, d1, d2, d3, r0, originalStart, sourceSpan, ref maxError, ref maxTime);
					if (r1 > 0f && r1 < 1f)
						EvaluateDifferenceAt(d0, d1, d2, d3, r1, originalStart, sourceSpan, ref maxError, ref maxTime);
				}
			}
			return true;
		}

		static void EvaluateDifferenceAt(float d0, float d1, float d2, float d3, float u,
			float startTime, float span, ref float maxError, ref float maxTime)
		{
			float difference = ((d3 * u + d2) * u + d1) * u + d0;
			float error = ManagedMath.Abs(difference);
			if (error > maxError)
			{
				maxError = error;
				maxTime = startTime + span * u;
			}
		}

		static float EvaluateKeyArray(ManagedKeyframe[] keys, int start, int end, float time)
		{
			int lo = start, hi = end;
			while (lo + 1 < hi)
			{
				int mid = (lo + hi) >> 1;
				if (keys[mid].time <= time) lo = mid;
				else hi = mid;
			}
			ManagedKeyframe a = keys[lo];
			ManagedKeyframe b = keys[ManagedMath.Min(lo + 1, end)];
			if (b.time - a.time <= k_TimeEpsilon) return a.value;
			return EvaluateTwoKeySegment(a, b, time);
		}

		static float EvaluateTwoKeySegment(ManagedKeyframe a, ManagedKeyframe b, float time)
		{
			float span = b.time - a.time;
			if (span <= 0f) return a.value;
			float u = ManagedMath.Clamp01((time - a.time) / span);
			float u2 = u * u;
			float u3 = u2 * u;
			return (2f * u3 - 3f * u2 + 1f) * a.value
				+ (u3 - 2f * u2 + u) * span * a.outTangent
				+ (-2f * u3 + 3f * u2) * b.value
				+ (u3 - u2) * span * b.inTangent;
		}

		static bool TryFitTangentsFast(ManagedAnimationCurve original, ManagedKeyframe[] keys,
			int start, int end, out float outTangent, out float inTangent)
		{
			outTangent = keys[start].outTangent;
			inTangent = keys[end].inTangent;
			if (keys[start].weightedMode != ManagedWeightedMode.None || keys[end].weightedMode != ManagedWeightedMode.None)
				return false;
			float span = keys[end].time - keys[start].time;
			if (span <= k_TimeEpsilon) return false;

			// Use a bounded sample count. This is only a fallback after the no-refit candidate fails.
			int steps = ManagedMath.Clamp(ManagedMath.CeilToInt(span * k_FitSteps), 8, 96);
			double aa = 0d, ab = 0d, bb = 0d, ar = 0d, br = 0d;
			for (int s = 1; s < steps; s++)
			{
				float u = s / (float)steps;
				float u2 = u * u;
				float u3 = u2 * u;
				float h00 = 2f * u3 - 3f * u2 + 1f;
				float h10 = u3 - 2f * u2 + u;
				float h01 = -2f * u3 + 3f * u2;
				float h11 = u3 - u2;
				float y = EvaluateKeyArray(keys, start, end, keys[start].time + span * u);
				float r = y - (h00 * keys[start].value + h01 * keys[end].value);
				float a = span * h10;
				float b = span * h11;
				aa += a * a;
				ab += a * b;
				bb += b * b;
				ar += a * r;
				br += b * r;
			}

			double determinant = aa * bb - ab * ab;
			if (Math.Abs(determinant) < 1e-12) return false;
			double m0 = (ar * bb - br * ab) / determinant;
			double m1 = (br * aa - ar * ab) / determinant;
			if (double.IsNaN(m0) || double.IsInfinity(m0) || double.IsNaN(m1) || double.IsInfinity(m1))
				return false;
			outTangent = (float)m0;
			inTangent = (float)m1;
			return true;
		}

		static ManagedKeyframe WithOutTangent(ManagedKeyframe key, float tangent)
		{
			key.outTangent = tangent;
			return key;
		}

		static ManagedKeyframe WithInTangent(ManagedKeyframe key, float tangent)
		{
			key.inTangent = tangent;
			return key;
		}

		static ManagedAnimationCurve BuildCurveFromPlan(ManagedAnimationCurve source, CurveReductionPlan plan)
		{
			if (source == null || plan == null || plan.NewKeys == null) return null;
			return new ManagedAnimationCurve(plan.NewKeys, plan.LeftModes, plan.RightModes, plan.Broken, source.PreWrapMode, source.PostWrapMode);
		}

		static bool CurveStaysWithinTolerance(ManagedAnimationCurve original, ManagedAnimationCurve reduced, float tolerance)
		{
			if (original == null || reduced == null || original.HasWeightedKeys() || reduced.HasWeightedKeys()) return false;
			ManagedKeyframe[] originalKeys = original.keys;
			if (originalKeys.Length == 0) return true;

			if (CanUseAnalyticValidation(original, reduced))
				return AnalyticCurveComparison(original, reduced, originalKeys, tolerance);

			for (int i = 0; i < originalKeys.Length; i++)
				if (ManagedMath.Abs(originalKeys[i].value - reduced.Evaluate(originalKeys[i].time)) > tolerance)
					return false;
			for (int i = 0; i < originalKeys.Length - 1; i++)
			{
				float t0 = originalKeys[i].time, t1 = originalKeys[i + 1].time;
				if (t1 - t0 > k_TimeEpsilon && !FallbackCompareInterval(original, reduced, t0, t1, tolerance, 0))
					return false;
			}
			return true;
		}

		static bool CanUseAnalyticValidation(ManagedAnimationCurve original, ManagedAnimationCurve reduced)
		{
			ManagedKeyframe[] a = original.keys, b = reduced.keys;
			for (int i = 0; i < a.Length; i++)
			{
				if (a[i].weightedMode != ManagedWeightedMode.None) return false;
				if (i < a.Length - 1 && original.GetRightTangentMode(i) == ManagedTangentMode.Constant) return false;
				if (i > 0 && original.GetLeftTangentMode(i) == ManagedTangentMode.Constant) return false;
			}
			for (int i = 0; i < b.Length; i++)
			{
				if (b[i].weightedMode != ManagedWeightedMode.None) return false;
				if (i < b.Length - 1 && reduced.GetRightTangentMode(i) == ManagedTangentMode.Constant) return false;
				if (i > 0 && reduced.GetLeftTangentMode(i) == ManagedTangentMode.Constant) return false;
			}
			return true;
		}

		static bool AnalyticCurveComparison(ManagedAnimationCurve original, ManagedAnimationCurve reduced,
			ManagedKeyframe[] originalKeys, float tolerance)
		{
			ManagedKeyframe[] reducedKeys = reduced.keys;
			int r = 0;
			for (int s = 0; s < originalKeys.Length - 1; s++)
			{
				float t0 = originalKeys[s].time, t1 = originalKeys[s + 1].time;
				if (t1 - t0 <= k_TimeEpsilon) continue;
				while (r + 1 < reducedKeys.Length && reducedKeys[r + 1].time <= t0 + k_TimeEpsilon) r++;
				if (r + 1 >= reducedKeys.Length) return false;
				if (!TryFindMaxCubicDifference(
					CubicFromKeyPair(reducedKeys[r], reducedKeys[r + 1]),
					reducedKeys[r].time, reducedKeys[r + 1].time - reducedKeys[r].time,
					CubicFromKeyPair(originalKeys[s], originalKeys[s + 1]),
					t0, t1, out float error, out _))
					return false;
				if (error > tolerance) return false;
			}
			return true;
		}

		static bool FallbackCompareInterval(ManagedAnimationCurve original, ManagedAnimationCurve reduced,
			float t0, float t1, float tolerance, int depth)
		{
			float tm = (t0 + t1) * 0.5f;
			float e0 = ManagedMath.Abs(original.Evaluate(t0) - reduced.Evaluate(t0));
			float em = ManagedMath.Abs(original.Evaluate(tm) - reduced.Evaluate(tm));
			float e1 = ManagedMath.Abs(original.Evaluate(t1) - reduced.Evaluate(t1));
			if (e0 > tolerance || em > tolerance || e1 > tolerance) return false;
			if (depth >= k_FallbackMaxValidationDepth) return true;
			if (ManagedMath.Max(e0, ManagedMath.Max(em, e1)) <= tolerance * k_ErrorComfortRatio) return true;
			return FallbackCompareInterval(original, reduced, t0, tm, tolerance, depth + 1)
				&& FallbackCompareInterval(original, reduced, tm, t1, tolerance, depth + 1);
		}

		// --- shared Transform reduction ------------------------------------------------

		static void OptimizeSharedTransformGroups(List<CurveWork> work)
		{
			if (work == null || work.Count == 0) return;
			var groups = BuildTransformGroups(work);
			foreach (KeyValuePair<string, List<CurveWork>> pair in groups)
				if (IsCompleteTransformGroup(pair.Value))
					RemoveCommonTransformKeys(pair.Value);
		}

		static bool ContainsManagedQuaternion(List<CurveWork> group)
		{
			bool x = false, y = false, z = false, w = false;
			for (int i = 0; i < group.Count; i++)
			{
				string p = group[i].PropertyName ?? string.Empty;
				if (EndsWithComponent(p, ".x")) x = true;
				else if (EndsWithComponent(p, ".y")) y = true;
				else if (EndsWithComponent(p, ".z")) z = true;
				else if (EndsWithComponent(p, ".w")) w = true;
			}
			return x && y && z && w;
		}

		static void RemoveCommonTransformKeys(List<CurveWork> group)
		{
			// Candidates are computed once. Removing a key cannot create a new common key time,
			// so the previous repeated do/while pass was unnecessary.
			List<SharedKeyCandidate> candidates = FindCommonInteriorKeyTimes(group);
			if (candidates.Count == 0) return;

			int count = group.Count;
			var originals = new ManagedAnimationCurve[count];
			for (int i = 0; i < count; i++) originals[i] = group[i].Original;
			List<float> validationTimes = BuildValidationTimes(originals);

			for (int i = candidates.Count - 1; i >= 0; i--)
				TryRemoveSharedTime(group, candidates[i], originals, validationTimes);
		}

		static List<SharedKeyCandidate> FindCommonInteriorKeyTimes(List<CurveWork> group)
		{
			var result = new List<SharedKeyCandidate>();
			if (group.Count == 0) return result;

			var keySets = new ManagedKeyframe[group.Count][];
			for (int g = 0; g < group.Count; g++)
				keySets[g] = group[g].Reduced.keys;

			ManagedKeyframe[] baseKeys = keySets[0];
			var cursors = new int[group.Count];

			for (int baseIndex = 1; baseIndex < baseKeys.Length - 1; baseIndex++)
			{
				float time = baseKeys[baseIndex].time;
				var indices = new int[group.Count];
				indices[0] = baseIndex;
				bool common = true;

				for (int g = 1; g < group.Count; g++)
				{
					ManagedKeyframe[] keys = keySets[g];
					int cursor = cursors[g];
					while (cursor < keys.Length && keys[cursor].time < time - k_TimeEpsilon) cursor++;
					cursors[g] = cursor;
					if (cursor >= keys.Length || cursor <= 0 || cursor >= keys.Length - 1
						|| ManagedMath.Abs(keys[cursor].time - time) > k_TimeEpsilon)
					{
						common = false;
						break;
					}
					indices[g] = cursor;
				}

				if (common)
					result.Add(new SharedKeyCandidate { Time = time, Indices = indices });
			}
			return result;
		}

		static bool TryRemoveSharedTime(List<CurveWork> group, SharedKeyCandidate candidate,
			ManagedAnimationCurve[] originals, List<float> validationTimes)
		{
			int count = group.Count;
			var candidates = new ManagedAnimationCurve[count];

			for (int i = 0; i < count; i++)
			{
				int index = candidate.Indices[i];
				ManagedAnimationCurve source = group[i].Reduced;
				if (index <= 0 || index >= source.length - 1) return false;
				ManagedAnimationCurve temp = CopyCurve(source);
				temp.RemoveKey(index);
				candidates[i] = temp;
			}

			CurveKind kind = group[0].Kind;
			bool ok = kind == CurveKind.Rotation
				? ValidateRotationGroup(originals, candidates, group, validationTimes)
				: kind == CurveKind.Position
					? ValidateVectorGroup(originals, candidates, group, validationTimes, false)
					: kind == CurveKind.Scale
						&& ValidateVectorGroup(originals, candidates, group, validationTimes, true);

			if (!ok) return false;
			for (int i = 0; i < count; i++) group[i].Reduced = candidates[i];
			return true;
		}

		static bool ValidateVectorGroup(ManagedAnimationCurve[] originals, ManagedAnimationCurve[] candidates,
			List<CurveWork> group, List<float> times, bool relative)
		{
			if (times == null || times.Count == 0) return true;

			float ratio = GetRelativeRatio(group);
			for (int i = 0; i < times.Count; i++)
			{
				ManagedVector3 a = EvaluateManagedVector3(originals, group, times[i]);
				ManagedVector3 b = EvaluateManagedVector3(candidates, group, times[i]);
				if (relative)
				{
					float dx = ManagedMath.Abs(a.x - b.x) / ManagedMath.Max(ManagedMath.Abs(a.x), 1e-3f);
					float dy = ManagedMath.Abs(a.y - b.y) / ManagedMath.Max(ManagedMath.Abs(a.y), 1e-3f);
					float dz = ManagedMath.Abs(a.z - b.z) / ManagedMath.Max(ManagedMath.Abs(a.z), 1e-3f);
					if (ManagedMath.Max(dx, ManagedMath.Max(dy, dz)) > ManagedMath.Max(ratio, 1e-7f)) return false;
				}
				else if (VectorGroupAbsoluteError(a, b, group) > 1f) return false;
			}
			return true;
		}

		static float VectorGroupAbsoluteError(ManagedVector3 original, ManagedVector3 candidate, List<CurveWork> group)
		{
			float xTolerance = GetComponentTolerance(group, ".x");
			float yTolerance = GetComponentTolerance(group, ".y");
			float zTolerance = GetComponentTolerance(group, ".z");
			float dx = ManagedMath.Abs(original.x - candidate.x) / ManagedMath.Max(xTolerance, 1e-7f);
			float dy = ManagedMath.Abs(original.y - candidate.y) / ManagedMath.Max(yTolerance, 1e-7f);
			float dz = ManagedMath.Abs(original.z - candidate.z) / ManagedMath.Max(zTolerance, 1e-7f);
			return ManagedMath.Max(dx, ManagedMath.Max(dy, dz));
		}

		static float GetComponentTolerance(List<CurveWork> group, string component)
		{
			for (int i = 0; i < group.Count; i++)
				if (EndsWithComponent(group[i].PropertyName ?? string.Empty, component))
					return group[i].Tolerance;
			return float.PositiveInfinity;
		}

		static bool ValidateRotationGroup(ManagedAnimationCurve[] originals, ManagedAnimationCurve[] candidates,
			List<CurveWork> group, List<float> times)
		{
			if (times == null || times.Count == 0) return true;
			bool quaternion = ContainsManagedQuaternion(group);
			float tolerance = 0f;
			for (int i = 0; i < group.Count; i++) tolerance = ManagedMath.Max(tolerance, group[i].Tolerance);

			for (int i = 0; i < times.Count; i++)
			{
				float t = times[i];
				ManagedQuaternion a = quaternion
					? NormalizeManagedQuaternion(EvaluateRotation4(originals, group, t))
					: ManagedQuaternion.Euler(EvaluateManagedVector3(originals, group, t));
				ManagedQuaternion b = quaternion
					? NormalizeManagedQuaternion(EvaluateRotation4(candidates, group, t))
					: ManagedQuaternion.Euler(EvaluateManagedVector3(candidates, group, t));
				if (ManagedQuaternion.Angle(a, b) > tolerance) return false;
			}
			return true;
		}

		static List<float> BuildValidationTimes(ManagedAnimationCurve[] originals)
		{
			int estimated = 0;
			for (int i = 0; i < originals.Length; i++) estimated += ManagedMath.Max(1, originals[i].length * 4);
			var times = new List<float>(estimated);
			for (int c = 0; c < originals.Length; c++)
			{
				ManagedKeyframe[] keys = originals[c].keys;
				for (int i = 0; i < keys.Length; i++)
				{
					times.Add(keys[i].time);
					if (i + 1 < keys.Length)
					{
						float t = keys[i].time, next = keys[i + 1].time;
						if (next > t)
						{
							times.Add(ManagedMath.Lerp(t, next, 0.25f));
							times.Add(ManagedMath.Lerp(t, next, 0.5f));
							times.Add(ManagedMath.Lerp(t, next, 0.75f));
						}
					}
				}
			}
			times.Sort();
			int write = 0;
			for (int i = 0; i < times.Count; i++)
				if (write == 0 || ManagedMath.Abs(times[i] - times[write - 1]) > k_TimeEpsilon)
					times[write++] = times[i];
			if (write < times.Count) times.RemoveRange(write, times.Count - write);
			return times;
		}

		static ManagedVector3 EvaluateManagedVector3(ManagedAnimationCurve[] curves, List<CurveWork> group, float time)
		{
			float x = 0f, y = 0f, z = 0f;
			for (int i = 0; i < group.Count; i++)
			{
				string p = group[i].PropertyName ?? string.Empty;
				float v = curves[i].Evaluate(time);
				if (EndsWithComponent(p, ".x")) x = v;
				else if (EndsWithComponent(p, ".y")) y = v;
				else if (EndsWithComponent(p, ".z")) z = v;
			}
			return new ManagedVector3(x, y, z);
		}

		static ManagedVector4 EvaluateRotation4(ManagedAnimationCurve[] curves, List<CurveWork> group, float time)
		{
			float x = 0f, y = 0f, z = 0f, w = 1f;
			for (int i = 0; i < group.Count; i++)
			{
				string p = group[i].PropertyName ?? string.Empty;
				float v = curves[i].Evaluate(time);
				if (EndsWithComponent(p, ".x")) x = v;
				else if (EndsWithComponent(p, ".y")) y = v;
				else if (EndsWithComponent(p, ".z")) z = v;
				else if (EndsWithComponent(p, ".w")) w = v;
			}
			return new ManagedVector4(x, y, z, w);
		}

		static ManagedQuaternion NormalizeManagedQuaternion(ManagedVector4 q)
		{
			float m = ManagedMath.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
			return m <= 1e-7f ? ManagedQuaternion.identity : new ManagedQuaternion(q.x / m, q.y / m, q.z / m, q.w / m);
		}

		static float GetRelativeRatio(List<CurveWork> group)
		{
			float ratio = float.PositiveInfinity;
			for (int i = 0; i < group.Count; i++) ratio = ManagedMath.Min(ratio, group[i].RelativeRatio);
			return float.IsInfinity(ratio) || float.IsNaN(ratio) ? 0f : ManagedMath.Max(ratio, 1e-7f);
		}

		static bool IsCompleteTransformGroup(List<CurveWork> group)
		{
			if (group == null || group.Count == 0) return false;
			CurveKind kind = group[0].Kind;
			bool x = false, y = false, z = false, w = false;
			for (int i = 0; i < group.Count; i++)
			{
				string p = group[i].PropertyName ?? string.Empty;
				if (EndsWithComponent(p, ".x")) x = true;
				else if (EndsWithComponent(p, ".y")) y = true;
				else if (EndsWithComponent(p, ".z")) z = true;
				else if (EndsWithComponent(p, ".w")) w = true;
			}
			return x && y && z && (kind != CurveKind.Rotation || !ContainsManagedQuaternion(group) || w);
		}

		static Dictionary<string, List<CurveWork>> BuildTransformGroups(List<CurveWork> work)
		{
			var groups = new Dictionary<string, List<CurveWork>>(StringComparer.Ordinal);
			for (int i = 0; i < work.Count; i++)
			{
				CurveWork item = work[i];
				if (item.Kind == CurveKind.Generic) continue;
				string representation = item.Kind == CurveKind.Rotation
					? GetRotationRepresentation(item.PropertyName)
					: string.Empty;
				string typeName = item.TypeName;
				string key = item.Kind + "\n" + (item.Path ?? string.Empty)
					+ "\n" + representation + "\n" + typeName;
				if (!groups.TryGetValue(key, out List<CurveWork> list))
				{
					list = new List<CurveWork>(4);
					groups.Add(key, list);
				}
				list.Add(item);
			}
			return groups;
		}

		static string GetRotationRepresentation(string property)
		{
			property = property ?? string.Empty;
			return property.IndexOf("m_LocalRotation", StringComparison.OrdinalIgnoreCase) >= 0
				? "ManagedQuaternion"
				: property.IndexOf("m_LocalEulerAngles", StringComparison.OrdinalIgnoreCase) >= 0
					|| property.IndexOf("localEulerAngles", StringComparison.OrdinalIgnoreCase) >= 0
					? "Euler"
					: "Rotation";
		}

		static void ValidateAndRevertTransformGroups(List<CurveWork> work)
		{
			if (work == null || work.Count == 0) return;
			var groups = BuildTransformGroups(work);
			foreach (KeyValuePair<string, List<CurveWork>> pair in groups)
			{
				List<CurveWork> group = pair.Value;
				if (!IsCompleteTransformGroup(group) || ValidateTransformGroup(group)) continue;
				for (int i = 0; i < group.Count; i++) group[i].Reduced = CopyCurve(group[i].Original);
			}
		}

		static bool ValidateTransformGroup(List<CurveWork> group)
		{
			int count = group.Count;
			var originals = new ManagedAnimationCurve[count];
			var candidates = new ManagedAnimationCurve[count];
			for (int i = 0; i < count; i++) { originals[i] = group[i].Original; candidates[i] = group[i].Reduced; }

			CurveKind kind = group[0].Kind;
			float ratio = GetRelativeRatio(group);
			float absTol = 0f;
			for (int i = 0; i < group.Count; i++)
				absTol = ManagedMath.Max(absTol, group[i].Tolerance);

			List<float> times = BuildValidationTimes(originals);
			for (int i = 0; i < times.Count; i++)
			{
				if (TransformErrorMagnitude(originals, candidates, group, kind, times[i], ratio, absTol) > 1f)
					return false;
			}

			// The sampled/key-time phase is the fast rejection pass. The adaptive interval pass
			// is retained as the final transform-level safety net for narrow deviations between
			// validation times. It is run only after all cheap checks pass.
			for (int i = 0; i + 1 < times.Count; i++)
			{
				float t0 = times[i], t1 = times[i + 1];
				if (t1 - t0 <= k_TimeEpsilon) continue;
				if (!AdaptiveTransformComparison(originals, candidates, group, kind,
					t0, t1, ratio, 0f, 0))
					return false;
			}
			return true;
		}

		static bool AdaptiveTransformComparison(ManagedAnimationCurve[] originals, ManagedAnimationCurve[] candidates,
			List<CurveWork> group, CurveKind kind, float t0, float t1,
			float ratio, float absoluteTolerance, int depth)
		{
			float tm = (t0 + t1) * 0.5f;
			float e0 = TransformErrorMagnitude(originals, candidates, group, kind, t0, ratio, absoluteTolerance);
			float em = TransformErrorMagnitude(originals, candidates, group, kind, tm, ratio, absoluteTolerance);
			float e1 = TransformErrorMagnitude(originals, candidates, group, kind, t1, ratio, absoluteTolerance);
			if (e0 > 1f || em > 1f || e1 > 1f) return false;
			if (depth >= 8) return true;
			float maxError = ManagedMath.Max(e0, ManagedMath.Max(em, e1));
			if (maxError <= k_ErrorComfortRatio) return true;
			return AdaptiveTransformComparison(originals, candidates, group, kind, t0, tm, ratio, absoluteTolerance, depth + 1)
				&& AdaptiveTransformComparison(originals, candidates, group, kind, tm, t1, ratio, absoluteTolerance, depth + 1);
		}

		static float TransformErrorMagnitude(ManagedAnimationCurve[] originals, ManagedAnimationCurve[] candidates,
			List<CurveWork> group, CurveKind kind, float time, float ratio, float unusedAbsoluteTolerance)
		{
			if (kind == CurveKind.Rotation)
			{
				ManagedQuaternion a, b;
				if (ContainsManagedQuaternion(group))
				{
					a = NormalizeManagedQuaternion(EvaluateRotation4(originals, group, time));
					b = NormalizeManagedQuaternion(EvaluateRotation4(candidates, group, time));
				}
				else
				{
					a = ManagedQuaternion.Euler(EvaluateManagedVector3(originals, group, time));
					b = ManagedQuaternion.Euler(EvaluateManagedVector3(candidates, group, time));
				}
				return ManagedQuaternion.Angle(a, b) / ManagedMath.Max(360f * ratio, 0.001f);
			}

			ManagedVector3 a3 = EvaluateManagedVector3(originals, group, time);
			ManagedVector3 b3 = EvaluateManagedVector3(candidates, group, time);
			if (kind == CurveKind.Scale)
			{
				float dx = ManagedMath.Abs(a3.x - b3.x) / ManagedMath.Max(ManagedMath.Abs(a3.x), 1e-3f);
				float dy = ManagedMath.Abs(a3.y - b3.y) / ManagedMath.Max(ManagedMath.Abs(a3.y), 1e-3f);
				float dz = ManagedMath.Abs(a3.z - b3.z) / ManagedMath.Max(ManagedMath.Abs(a3.z), 1e-3f);
				return ManagedMath.Max(dx, ManagedMath.Max(dy, dz)) / ManagedMath.Max(ratio, 1e-7f);
			}
			return VectorGroupAbsoluteError(a3, b3, group);
		}


        public static ManagedReductionResult ReduceClip(IReadOnlyList<ManagedCurveWork> input, float tolerancePercent)
        {
            if (input == null || input.Count == 0 || tolerancePercent <= 0f) return new ManagedReductionResult();
            var work = new List<CurveWork>(input.Count);
            for (int i = 0; i < input.Count; i++)
            {
                var source = input[i];
                if (source == null || source.Curve == null || source.Curve.length < 3 || !source.Curve.IsFinite() || source.Curve.HasWeightedKeys()) continue;
                float tolerance = source.Tolerance > 0f ? source.Tolerance : ComputeTolerance(source.Curve, source.Kind, tolerancePercent);
                if (tolerance <= 0f) continue;
                if (source.Curve.IsConstant(out _) && source.Curve.IsSafeConstant()) continue;
                CurveKind kind = source.Kind;
                var reduced = ComputeReductionPlan(source.Curve, tolerance, true);
                work.Add(new CurveWork
                {
                    Path = source.Path ?? string.Empty,
                    PropertyName = source.PropertyName ?? string.Empty,
                    TypeName = source.TypeName ?? string.Empty,
                    Original = source.Curve.Copy(),
                    Reduced = reduced == null ? source.Curve.Copy() : BuildCurveFromPlan(source.Curve, reduced),
                    Kind = kind,
                    Tolerance = tolerance,
                    RelativeRatio = tolerancePercent * 0.01f
                });
            }
            OptimizeSharedTransformGroups(work);
            ValidateAndRevertTransformGroups(work);
            var result = new ManagedReductionResult();
            for (int i = 0; i < work.Count; i++)
            {
                var item = work[i];
                if (!CurveStaysWithinTolerance(item.Original, item.Reduced, item.Tolerance)) continue;
                if (CurvesEquivalent(item.Original, item.Reduced)) continue;
                int removed = item.Original.length - item.Reduced.length;
                if (removed <= 0) continue;
                result.Curves.Add(new ManagedCurveReduction
                {
                    BindingKey = MakeBindingKey(item.Path, item.PropertyName, item.TypeName),
                    Reduced = item.Reduced.Copy(),
                    RemovedKeys = removed
                });
                result.RemovedKeys += removed;
            }
            return result;
        }

        static float ComputeTolerance(ManagedAnimationCurve curve, CurveKind kind, float percent)
        {
            float ratio = ManagedMath.Max(0f, percent) * 0.01f;
            if (ratio <= 0f) return 0f;
            if (kind == CurveKind.Rotation) return ManagedMath.Max(0.001f, 360f * ratio);
            var keys = curve.keys; float maxAbs = 0f, min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < keys.Length; i++) { float v=keys[i].value, a=ManagedMath.Abs(v); if(a>maxAbs)maxAbs=a; if(v<min)min=v; if(v>max)max=v; }
            if (kind == CurveKind.Position || kind == CurveKind.Scale) return ManagedMath.Max(1e-7f, ManagedMath.Max(maxAbs,1e-3f)*ratio);
            float range=max-min; if (!(range>0f)||float.IsNaN(range)||float.IsInfinity(range)) range=ManagedMath.Max(maxAbs,1f);
            return ManagedMath.Max(1e-7f, range*ratio);
        }

        static string MakeBindingKey(string path, string property, string typeName)
            => (path ?? string.Empty) + "\n" + (property ?? string.Empty) + "\n" + (typeName ?? string.Empty);

        static bool EndsWithComponent(string property, string component)
            => !string.IsNullOrEmpty(property) && property.EndsWith(component, StringComparison.OrdinalIgnoreCase);

        static bool CurvesEquivalent(ManagedAnimationCurve a, ManagedAnimationCurve b)
        {
            if (a == null || b == null || a.length != b.length) return false;
            var ak=a.keys; var bk=b.keys;
            for(int i=0;i<ak.Length;i++)
            {
                if(ak[i].time!=bk[i].time || ak[i].value!=bk[i].value || ak[i].inTangent!=bk[i].inTangent || ak[i].outTangent!=bk[i].outTangent || ak[i].inWeight!=bk[i].inWeight || ak[i].outWeight!=bk[i].outWeight || ak[i].weightedMode!=bk[i].weightedMode || ak[i].LeftMode!=bk[i].LeftMode || ak[i].RightMode!=bk[i].RightMode || ak[i].Broken!=bk[i].Broken) return false;
            }
            return true;
        }

        static ManagedAnimationCurve CopyCurve(ManagedAnimationCurve source) => source?.Copy();

        static bool HasNonFiniteKeys(ManagedAnimationCurve curve)
        {
            var keys=curve?.keys; if(keys==null) return false;
            for(int i=0;i<keys.Length;i++){var k=keys[i]; if(float.IsNaN(k.time)||float.IsInfinity(k.time)||float.IsNaN(k.value)||float.IsInfinity(k.value)||float.IsNaN(k.inTangent)||float.IsInfinity(k.inTangent)||float.IsNaN(k.outTangent)||float.IsInfinity(k.outTangent)) return true;}
            return false;
        }

    }
}

namespace KOF98.Game
{
    /// <summary>
    /// Collision detection and pushbox resolution over ECS components.
    /// </summary>
    public static class CollisionSystem
    {
        /// <summary>
        /// Check whether the attacker's hitbox of the given group overlaps any
        /// opponent's hurtbox. Returns the first hit target's entity index, or
        /// -1 if no hit.
        /// </summary>
        public static int CheckAttackHit(GameWorld world, int attacker, int groupId)
        {
            if (!world.IsAliveSlot(attacker)) return -1;

            FRect hitbox = FRect.Empty;
            int hitCount = world.HitBoxCounts[attacker];
            for (int i = 0; i < hitCount; i++)
            {
                ref var entry = ref world.HitBox(attacker, i);
                if (entry.GroupId == groupId) { hitbox = entry.Box; break; }
            }
            if (hitbox.IsEmpty) return -1;

            int facingSign = world.Transform[attacker].FacingSign;
            hitbox.GetWorldBounds(world.Transform[attacker].Position, facingSign,
                out float hMinX, out float hMinY, out float hMaxX, out float hMaxY);

            int attackerTeam = world.Identity[attacker].Team;

            for (int t = 0; t < GameConstants.MaxCharacters; t++)
            {
                if (t == attacker) continue;
                if (!world.IsAliveSlot(t) || world.Kinds[t] != EntityKind.Character) continue;
                if (world.Identity[t].Team == attackerTeam) continue;
                if (!world.Life[t].IsAlive) continue;

                int targetFacing = world.Transform[t].FacingSign;
                int hurtCount = world.HurtBoxCounts[t];
                for (int j = 0; j < hurtCount; j++)
                {
                    ref var hurt = ref world.HurtBox(t, j);
                    hurt.GetWorldBounds(world.Transform[t].Position, targetFacing,
                        out float tMinX, out float tMinY, out float tMaxX, out float tMaxY);

                    if (FRect.Overlaps(hMinX, hMinY, hMaxX, hMaxY, tMinX, tMinY, tMaxX, tMaxY))
                        return t;
                }
            }
            return -1;
        }

        public static int CheckAttackBlocked(GameWorld world, int attacker, int groupId)
        {
            if (!world.IsAliveSlot(attacker)) return -1;

            FRect hitbox = FRect.Empty;
            int hitCount = world.HitBoxCounts[attacker];
            for (int i = 0; i < hitCount; i++)
            {
                ref var entry = ref world.HitBox(attacker, i);
                if (entry.GroupId == groupId) { hitbox = entry.Box; break; }
            }
            if (hitbox.IsEmpty) return -1;

            int facingSign = world.Transform[attacker].FacingSign;
            hitbox.GetWorldBounds(world.Transform[attacker].Position, facingSign,
                out float hMinX, out float hMinY, out float hMaxX, out float hMaxY);

            int attackerTeam = world.Identity[attacker].Team;

            for (int t = 0; t < GameConstants.MaxCharacters; t++)
            {
                if (t == attacker) continue;
                if (!world.IsAliveSlot(t) || world.Kinds[t] != EntityKind.Character) continue;
                if (world.Identity[t].Team == attackerTeam) continue;
                if (!world.Life[t].IsAlive) continue;

                if (!world.HasTag(t, GameConstants.TAG_BLOCK)) continue;
                if (world.BlockBoxes[t].IsEmpty) continue;

                int targetFacing = world.Transform[t].FacingSign;
                world.BlockBoxes[t].GetWorldBounds(world.Transform[t].Position, targetFacing,
                    out float tMinX, out float tMinY, out float tMaxX, out float tMaxY);

                if (FRect.Overlaps(hMinX, hMinY, hMaxX, hMaxY, tMinX, tMinY, tMaxX, tMaxY))
                    return t;
            }
            return -1;
        }

        /// <summary>
        /// Resolve pushbox overlaps between alive characters. Pushes the
        /// pair apart equally and re-clamps to stage bounds.
        /// </summary>
        public static void ResolvePushBoxes(GameWorld world)
        {
            for (int i = 0; i < GameConstants.MaxCharacters; i++)
            {
                if (!world.IsAliveSlot(i) || world.Kinds[i] != EntityKind.Character) continue;
                if (!world.Life[i].IsAlive) continue;

                for (int j = i + 1; j < GameConstants.MaxCharacters; j++)
                {
                    if (!world.IsAliveSlot(j) || world.Kinds[j] != EntityKind.Character) continue;
                    if (!world.Life[j].IsAlive) continue;

                    ResolvePair(world, i, j);
                }
            }
        }

        private static void ResolvePair(GameWorld world, int a, int b)
        {
            ref var aTr = ref world.Transform[a];
            ref var bTr = ref world.Transform[b];

            world.PushBoxes[a].GetWorldBounds(aTr.Position, aTr.FacingSign,
                out float aMinX, out float aMinY, out float aMaxX, out float aMaxY);
            world.PushBoxes[b].GetWorldBounds(bTr.Position, bTr.FacingSign,
                out float bMinX, out float bMinY, out float bMaxX, out float bMaxY);

            if (!FRect.Overlaps(aMinX, aMinY, aMaxX, aMaxY, bMinX, bMinY, bMaxX, bMaxY))
                return;

            float overlapLeft = aMaxX - bMinX;
            float overlapRight = bMaxX - aMinX;
            float push = (overlapLeft < overlapRight ? overlapLeft : overlapRight) * 0.5f;

            if (aTr.Position.X < bTr.Position.X)
            {
                aTr.Position = new FVec2(aTr.Position.X - push, aTr.Position.Y);
                bTr.Position = new FVec2(bTr.Position.X + push, bTr.Position.Y);
            }
            else
            {
                aTr.Position = new FVec2(aTr.Position.X + push, aTr.Position.Y);
                bTr.Position = new FVec2(bTr.Position.X - push, bTr.Position.Y);
            }

            PhysicsSystem.ClampToStage(ref aTr.Position);
            PhysicsSystem.ClampToStage(ref bTr.Position);
        }
    }
}
